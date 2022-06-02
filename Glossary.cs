﻿using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Azure.AI.Translation.Document;

namespace DocumentTranslationService.Core
{
    /// <summary>
    /// Holds the glossary and the functions to maintain it. 
    /// </summary>
    public class Glossary
    {
        /// <summary>
        /// Dictionary of Glossary Filename and various glossary information
        /// The string is the file name.
        /// </summary>
        public Dictionary<string, TranslationGlossary> Glossaries { get; private set; } = new();
        /// <summary>
        /// Dictionary of plain Uri glossaries
        /// For use with Managed Identity
        /// </summary>
        public Dictionary<string, TranslationGlossary> PlainUriGlossaries { get; private set; }

        /// <summary>
        /// Holds the Container for the glossary files
        /// </summary>
        private BlobContainerClient containerClient;
        private readonly DocumentTranslationService translationService;

        /// <summary>
        /// Fires when a file submitted as glossary was not used.
        /// </summary>
        public event EventHandler<List<string>> OnGlossaryDiscarded;

        /// <summary>
        /// Fires when the upload complete.
        /// Returns the number of files uploaded, and the combined size.
        /// </summary>
        public event EventHandler<(int, long)> OnUploadComplete;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="translationService"></param>
        /// <param name="glossaryFiles"></param>
        public Glossary(DocumentTranslationService translationService, List<string> glossaryFiles)
        {
            if ((glossaryFiles is null) || (glossaryFiles.Count == 0))
            {
                Glossaries = null;
                return;
            }
            foreach (string file in glossaryFiles)
            {
                Glossaries.TryAdd(file, null);
            }
            this.translationService = translationService;
        }

        /// <summary>
        /// Upload the glossary files named in the GlossaryFiles property.
        /// </summary>
        /// <param name="storageConnectionString">The storage connection string to use for container creation</param>
        /// <param name="containerNameBase">The GUID-infused base name to use as the container name</param>
        /// <returns>Task</returns>
        /// <remarks>Serious optimization possible here. The container should be permanent, and upload only changed files, or no files at all, and still use them.</remarks>
        public async Task<(int, long)> UploadAsync(string storageConnectionString, string containerNameBase)
        {
            //Expand directory
            if (Glossaries is null) return (0, 0);
            foreach (var glossary in Glossaries)
            {
                if (File.GetAttributes(glossary.Key) == FileAttributes.Directory)
                {
                    Glossaries.Remove(glossary.Key);
                    foreach (var file in Directory.EnumerateFiles(glossary.Key))
                    {
                        Glossaries.Add(file, null);
                    }
                }
            }
            //Remove files that don't match the allowed extensions
            List<string> discards = new();
            foreach (var glossary in Glossaries)
            {
                if (!(translationService.GlossaryExtensions.Contains(Path.GetExtension(glossary.Key))))
                {
                    Glossaries.Remove(glossary.Key);
                    discards.Add(glossary.Key);
                }
            }
            if (discards is not null)
                foreach (string fileName in discards)
                {
                    Debug.WriteLine($"Glossary files ignored: {fileName}");
                    if (OnGlossaryDiscarded is not null) OnGlossaryDiscarded(this, discards);
                }
            //Exit if no files are left
            if (Glossaries.Count == 0)
            {
                Glossaries = null;
                return (0, 0);
            }

            //Create glossary container
            Debug.WriteLine("START - glossary container creation.");
            BlobContainerClient glossaryContainer = new(storageConnectionString, containerNameBase + "gls");
            await glossaryContainer.CreateIfNotExistsAsync();
            this.containerClient = glossaryContainer;

            //Do the upload
            Debug.WriteLine("START - glossary upload.");
            System.Threading.SemaphoreSlim semaphore = new(10); //limit the number of concurrent uploads
            PlainUriGlossaries = new(Glossaries);
            int fileCounter = 0;
            long uploadSize = 0;
            List<Task> uploads = new();
            foreach (var glossary in Glossaries)
            {
                await semaphore.WaitAsync();
                try
                {
                    var filename = glossary.Key;
                    BlobClient blobClient = new(translationService.StorageConnectionString, glossaryContainer.Name, DocumentTranslationBusiness.Normalize(filename));
                    uploads.Add(blobClient.UploadAsync(filename, true));
                    Uri sasUriGlossaryBlob = blobClient.GenerateSasUri(BlobSasPermissions.All, DateTimeOffset.UtcNow + TimeSpan.FromHours(5));
                    Debug.WriteLine($"Glossary URI: {sasUriGlossaryBlob.AbsoluteUri}");
                    TranslationGlossary translationGlossary = new(sasUriGlossaryBlob, Path.GetExtension(glossary.Key)[1..].ToUpperInvariant());
                    Glossaries[glossary.Key] = translationGlossary;
                    TranslationGlossary plainUriTranslationGlossary = new(blobClient.Uri, Path.GetExtension(glossary.Key)[1..].ToUpperInvariant());
                    PlainUriGlossaries[glossary.Key] = plainUriTranslationGlossary;
                    fileCounter++;
                    uploadSize += new FileInfo(filename).Length;
                    semaphore.Release();
                    Debug.WriteLine(String.Format($"Glossary file {filename} uploaded."));
                }
                catch (System.IO.IOException ex)
                {
                    Debug.WriteLine(ex.Message);
                    throw;
                }
            }
            await Task.WhenAll(uploads);
            Debug.WriteLine($"Glossary: {fileCounter} files, {uploadSize} bytes uploaded.");
            if (OnUploadComplete is not null) OnUploadComplete(this, (fileCounter, uploadSize));
            return (fileCounter, uploadSize);
        }

        public async Task<Azure.Response> DeleteAsync()
        {
            if (Glossaries is not null)
            {
                Azure.Response response;
                try
                {
                    response = await containerClient.DeleteAsync();
                }
                catch (Azure.RequestFailedException)
                {
                    Debug.WriteLine("Glossary deletion failed.");
                    throw;
                }
                return response;
            }
            return null;
        }
    }
}
