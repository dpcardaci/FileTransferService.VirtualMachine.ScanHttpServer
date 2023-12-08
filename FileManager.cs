using System;
using System.IO;
using System.Threading.Tasks;
using Serilog;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Configuration;
using Azure.Identity;
using Azure;

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
            Log.Information("Begin downloading to temp file");

            string appConfigurationConnString = Environment.GetEnvironmentVariable("APP_CONFIGURATION_CONN_STRING", EnvironmentVariableTarget.Machine);
            var builder = new ConfigurationBuilder();
            var configuration = builder.AddAzureAppConfiguration(options =>
                options.Connect(appConfigurationConnString)
                    .ConfigureKeyVault(kv =>
                    {
                        kv.SetCredential(new ClientSecretCredential(
                            Environment.GetEnvironmentVariable("AZURE_TENANT_ID", EnvironmentVariableTarget.Machine),
                            Environment.GetEnvironmentVariable("AZURE_CLIENT_ID", EnvironmentVariableTarget.Machine),
                            Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET", EnvironmentVariableTarget.Machine),
                            new ClientSecretCredentialOptions
                            {
                                AuthorityHost = AzureAuthorityHosts.AzureGovernment
                            }
                        ));
                    })
            ).Build();

            string tempFileName = Path.GetTempFileName();
            Log.Information($"Temp file name: {tempFileName}");

            string baseStoragePath = "blob.core.usgovcloudapi.net";
            string accountName = configuration["UploadStorageAccountName"];
            Log.Information($"Account name: {accountName}");

            string accountSas = configuration["UploadStorageAccountSasToken"];
            Log.Information($"Account Sas: {accountSas}");

            string path = $"https://{accountName}.{baseStoragePath}/{blobContainer}/{blobName}";
            Log.Information($"Path: {path}");

            Uri fileUri = new Uri(path);

            try
            {
                Log.Information("Create BlobBlockCient");
                AzureSasCredential credential = new AzureSasCredential(accountSas);
                BlockBlobClient blockBlobClient = new BlockBlobClient(fileUri, credential);

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
