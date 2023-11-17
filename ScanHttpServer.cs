using HttpMultipartParser;
using System.Text.Json;
using Serilog;
using Serilog.Exceptions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using System.Reflection;
using System.Linq;
using Azure.Messaging.EventGrid;
using System.ComponentModel.DataAnnotations;
using FileTransferService.Core;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using Azure.Identity;
using Microsoft.Extensions.Hosting;


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

                    Stream requestInputStream  = new MemoryStream();
                    await request.InputStream.CopyToAsync(requestInputStream);
                    requestInputStream.Position = 0;

                    Log.Information("Starting a new task to begin scanning");
                    Task.Run(() => ScanRequest(requestInputStream));

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
                RaiseEventGridEvent(ScanEventGridEventType.Error, 
                                               new ScanError("Wrong request Content-type"));            
                Log.Error("Wrong request Content-type for scanning, {requestContentType}", request.ContentType);
                SendResponse(response, HttpStatusCode.BadRequest, new { ErrorMessage = $"Wrong request Content-type: {request.ContentType}" });
                return;
            };
        }

        public static void ScanRequest(object data)
        {
            Log.Information("Scan request initiated");
            try
            {   
                JsonSerializer serializer = new JsonSerializer();
                TransferInfo transferInfo = serializer.Deserialize<TransferInfo>(new JsonTextReader(new StreamReader((Stream)data)));
                            
                //var requestParameters = (Stream)data;
                var scanner = new WindowsDefenderScanner();
                //var parser = MultipartFormDataParser.Parse(requestParameters);
                
                //Log.Information("Parsing request parameters");
                //string fileName = parser.GetParameterValue("fileName");
                //string filePath = parser.GetParameterValue("filePath");

                Log.Information($"Beginning to download file: {transferInfo.FileName} from: {transferInfo.FilePath}");
                string tempFileName = FileUtilities.DownloadToTempFileAsync(transferInfo.FileName, transferInfo.FilePath).GetAwaiter().GetResult();

                if (tempFileName == null)
                {    
                    RaiseEventGridEvent(ScanEventGridEventType.Error, 
                                                   new ScanError("Can't save the file received in the request"));
                    Log.Error("Can't save the file received in the request");
                    return;
                }

                Log.Information($"Scanning file: {fileName}");
                var result = scanner.Scan(tempFileName);

                if(result.IsError)
                {
                    RaiseEventGridEvent(ScanEventGridEventType.Error, 
                                                   new ScanError($"Error during the scan Error message: {result.ErrorMessage}"));
                    Log.Error($"Error during the scan Error message: {result.ErrorMessage}");
                    return;
                }

                // TransferInfo transferInfo = new TransferInfo
                // {
                //     FileName = fileName,
                //     FilePath = filePath,
                //     ScanInfo = new ScanInfo
                //     {
                //         IsThreat = result.IsThreat,
                //         ThreatType = result.ThreatType
                //     }
                    
                // };
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
                    RaiseEventGridEvent(ScanEventGridEventType.Error, new ScanError($"Exception caught when trying to delete temp file: {tempFileName}."));
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
            string responseString = JsonConvert.SerializeObject(responseData);
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
