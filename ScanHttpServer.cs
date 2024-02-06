using System.Text.Json;
using Serilog;
using Serilog.Exceptions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using System.Reflection;
using Azure.Messaging.EventGrid;
using FileTransferService.Core;
using Microsoft.Extensions.Configuration;
using Azure.Identity;
using Azure.Core;

namespace ScanHttpServer
{
    public class ScanHttpServer
    {
        private enum requestType { SCAN, DEFAULT }

        public static async Task HandleRequestAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            Log.Information("Got new request {requestUrl}", request.Url);
            Log.Information("Raw URL: {requestRawUrl}", request.RawUrl);
            Log.Information("request.ContentType: {requestContentType}", request.ContentType);

            var requestTypeTranslation = new Dictionary<string, requestType>
            {
                { "/scan", requestType.SCAN },
                { "/", requestType.DEFAULT },
                { "", requestType.DEFAULT }
            };

            requestType type = requestTypeTranslation[request.RawUrl];

            switch (type)
            {
                case requestType.SCAN:
                    Log.Information("Scan request received");
                    TestRequestContentType(request, response);

                    TransferInfo transferInfo = GetTransferInfoFromRequest(request);

                    Log.Information("Starting a new task to begin scanning");
                    Task.Run(() => ScanRequest(transferInfo));

                    Log.Information("Respond with OK to scan request");
                    SendResponse(response, HttpStatusCode.Accepted, new {});
                    break;
                case requestType.DEFAULT:
                    SendResponse(response, HttpStatusCode.OK, new {});
                    break;
                default:
                    Log.Information("No valid request type");
                    break;
            }
            Log.Information("Done Handling Request {requestUrl}", request.Url);
        }

        private static void TestRequestContentType(HttpListenerRequest request, HttpListenerResponse response) 
        {
            Log.Information("Testing request content type");
            if (!request.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            {  
                TransferInfo transferInfo = GetTransferInfoFromRequest(request);
                TransferError transferError = CreateTransferError(transferInfo, $"Wrong request Content-type: {request.ContentType}");

                RaiseEventGridEvent(ScanEventGridEventType.Error, transferError);            
                Log.Error("Wrong request Content-type for scanning, {requestContentType}", request.ContentType);
                SendResponse(response, HttpStatusCode.BadRequest, new { ErrorMessage = $"Wrong request Content-type: {request.ContentType}" });
                return;
            };
        }

        public static void ScanRequest(TransferInfo transferInfo)
        {
            Log.Information("Scan request initiated");
            try
            {                   
                var scanner = new WindowsDefenderScanner();

                Log.Information($"Beginning to download file: {transferInfo.FileName} from: {transferInfo.FilePath}");
                string tempFileName = FileUtilities.DownloadToTempFileAsync(transferInfo.FileName, transferInfo.FilePath).GetAwaiter().GetResult();

                if (tempFileName == null)
                {
                    TransferError transferError = CreateTransferError(transferInfo, "Can't save the file received in the request");

                    RaiseEventGridEvent(ScanEventGridEventType.Error, transferError);
                    Log.Error("Can't save the file received in the request");
                    return;
                }

                Log.Information($"Scanning file: {transferInfo.FileName}");
                var result = scanner.Scan(tempFileName);

                if(result.IsError)
                {
                    TransferError transferError = CreateTransferError(transferInfo, $"Error during the scan Error message: {result.ErrorMessage}");

                    RaiseEventGridEvent(ScanEventGridEventType.Error, transferError);
                    Log.Error($"Error during the scan Error message: {result.ErrorMessage}");
                    return;
                }

                transferInfo.ScanInfo = new ScanInfo
                {
                    IsThreat = result.IsThreat,
                    ThreatType = result.ThreatType
                };

                try
                {
                    File.Delete(tempFileName);
                }
                catch (Exception e)
                {
                    TransferError transferError = CreateTransferError(transferInfo, $"Exception caught when trying to delete temp file: {tempFileName}.");

                    RaiseEventGridEvent(ScanEventGridEventType.Error, transferError);
                    Log.Error(e, $"Exception caught when trying to delete temp file: {tempFileName}.");
                    return;
                }
            
                RaiseEventGridEvent(ScanEventGridEventType.Completed, transferInfo);
                Log.Information($"Scan completed: {transferInfo}");
            }
            catch(Exception e)
            {
                Log.Error(e, "Exception caught when trying to scan");
            }
        }

        private static void SendResponse(
            HttpListenerResponse response,
            HttpStatusCode statusCode,
            object responseData)
        {
            response.StatusCode = (int)statusCode;
            string responseString = JsonSerializer.Serialize(responseData);
            byte[] buffer = System.Text.Encoding.UTF8.GetBytes(responseString);
            response.ContentLength64 = buffer.Length;
            var responseOutputStream = response.OutputStream;
            try
            {
                responseOutputStream.Write(buffer, 0, buffer.Length);
            }
            finally
            {
                Log.Information("Sending response, {statusCode}:{responseString}", statusCode, responseString);
                responseOutputStream.Close();
            }
        }

        private static void RaiseEventGridEvent(ScanEventGridEventType scanEventGridEventType, object data) 
        {
            string appConfigurationConnString = Environment.GetEnvironmentVariable("APP_CONFIGURATION_CONN_STRING", EnvironmentVariableTarget.Machine);
            var builder = new ConfigurationBuilder();
            var _configuration = builder.AddAzureAppConfiguration(options =>
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

            EventGridPublisherClient scanCompletedPublisher = new EventGridPublisherClient(
                new Uri(_configuration["ScanCompletedTopicUri"]), 
                new Azure.AzureKeyCredential(_configuration["ScanCompletedTopicKey"]));

            EventGridPublisherClient scanErrorPublisher = new EventGridPublisherClient(
                new Uri(_configuration["ScanErrorTopicUri"]), 
                new Azure.AzureKeyCredential(_configuration["ScanErrorTopicKey"]));

            EventGridEvent scanEventGridEvent = new EventGridEvent
            (
                "FileTransferService/Scan",
                scanEventGridEventType.ToString(),
                "1.0", 
                data
            );

            switch (scanEventGridEventType)
            {
                case ScanEventGridEventType.Completed:
                    scanCompletedPublisher.SendEvent(scanEventGridEvent);
                    break;
                case ScanEventGridEventType.Error:
                    scanErrorPublisher.SendEvent(scanEventGridEvent);
                    break;
                default:
                    Log.Information("No valid ScanEventGridEventType");
                    break;
            }
        }

        private static TransferInfo GetTransferInfoFromRequest(HttpListenerRequest request)
        {
            Log.Information("Getting transfer info from request");
            StreamReader streamReader = new StreamReader(request.InputStream);
            string transferInfoJsonString = streamReader.ReadToEnd();
            Log.Information("transferInfoJsonString: {transferInfoJsonString}", transferInfoJsonString);

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            TransferInfo transferInfo = JsonSerializer.Deserialize<TransferInfo>(transferInfoJsonString, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return transferInfo;
        }

        private static TransferError CreateTransferError(TransferInfo transferInfo, string message)
        {
            TransferError transferError = new TransferError
            {
                TransferId = transferInfo.TransferId,
                OriginatingUserPrincipalName = transferInfo.OriginatingUserPrincipalName,
                OnBehalfOfUserPrincipalName = transferInfo.OnBehalfOfUserPrincipalName,
                OriginationDateTime = transferInfo.OriginationDateTime,
                FileName = transferInfo.FileName,
                Message = message,
            };
            return transferError;
        }
        
        public static void SetUpLogger(string logFileName)
        {
            string runDirPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string logFilePath = Path.Combine(runDirPath, "log", logFileName);
            Log.Logger = new LoggerConfiguration()
                .Enrich.WithExceptionDetails()
                .WriteTo.File(logFilePath, rollingInterval: RollingInterval.Day)
                .WriteTo.Console()
                .MinimumLevel.Debug()
                .CreateLogger();
        }

        public static void Main(string[] args)
        {
            int httpsPort = 443;
            int httpPort = 80;
            string[] prefix = {
                $"https://+:{httpsPort}/",
                $"http://+:{httpPort}/"
            };

            SetUpLogger("ScanHttpServer-.log");
            var listener = new HttpListener();

            foreach (string s in prefix)
            {
                listener.Prefixes.Add(s);
            }

            listener.Start();
            Log.Information("Starting ScanHttpServer");

            while (true)
            {
                Log.Information("Waiting for requests...");
                var context = listener.GetContext();
                Task.Run(() => HandleRequestAsync(context));
            }
        }
    }
}
