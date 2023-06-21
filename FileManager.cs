using System;
using System.IO;
using System.Threading.Tasks;
using Serilog;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Blobs.Models;

namespace ScanHttpServer
{
    public static class FileUtilities
    {
       public static string SaveToTempFile(Stream fileData)
        {
            string tempFileName = Path.GetTempFileName();
            Log.Information("tmpFileName: {tempFileName}", tempFileName);
            try
            {
                using (var fileStream = File.OpenWrite(tempFileName))
                {
                    fileData.CopyTo(fileStream);
                }
                Log.Information("File created Successfully");
                return tempFileName;
            }
            catch (Exception e)
            {
                Log.Error(e, "Exception caught when trying to save temp file {tempFileName}.", tempFileName);
                return null;
            }
        }

        public static async Task<string> DownloadToTempFileAsync(string blobName, string blobContainer)
        {
            Log.Information("In DownloadToTempFile function.");

            string tempFileName = Path.GetTempFileName();
            Log.Information("tmpFileName: {tempFileName}", tempFileName);

            string baseStoragePath = "blob.core.usgovcloudapi.net";
            string accountName = Environment.GetEnvironmentVariable("FtsStorageAccountName", EnvironmentVariableTarget.Machine);
            Log.Information($"Account name: {accountName}");

            string accountKey = Environment.GetEnvironmentVariable("FtsStorageAccountKey", EnvironmentVariableTarget.Machine);
            Log.Information($"Account key: {accountKey}");

            string path = $"https://{accountName}.{baseStoragePath}/{blobContainer}/{blobName}";
            Log.Information($"path: {path}");

            Uri blobUri = new Uri(path);


            try
            {

                Log.Information("Create BlobBlockCient");
                StorageSharedKeyCredential credential = new StorageSharedKeyCredential(accountName, accountKey);
                BlockBlobClient blockBlobClient = new BlockBlobClient(blobUri, credential);

                await blockBlobClient.DownloadToAsync(tempFileName);
                Log.Information("File created Successfully");

                return tempFileName;
            }
            catch (Exception e)
            {
                Log.Error(e, "Exception caught when trying to save temp file {tempFileName}.", tempFileName);
                return null;
            }
        }
    }
}
