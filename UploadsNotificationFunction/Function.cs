using System;
using System.Text.Json;
using System.Threading.Tasks;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace UploadsNotificationFunction;

public class Function
{
    private readonly IAmazonSimpleNotificationService _snsClient;
    private readonly string _snsTopicArn;

    public Function()
    {
        _snsClient = new AmazonSimpleNotificationServiceClient();
        _snsTopicArn = GetSSMParameter("/azcx/sns/uploads-notification-topic-arn");
    }

    // For unit testing
    public Function(IAmazonSimpleNotificationService snsClient, string snsTopicArn)
    {
        _snsClient = snsClient;
        _snsTopicArn = snsTopicArn;
    }

    public async Task Handler(SQSEvent evnt, ILambdaContext context)
    {
        foreach (var record in evnt.Records)
        {
            try
            {
                var imageInfo = JsonSerializer.Deserialize<ImageInfo>(record.Body);
                var snsMessage = $"An image has been uploaded:\n\n" +
                                 $"Name: {imageInfo?.Name}\n" +
                                 $"Size: {imageInfo?.Size} bytes\n" +
                                 $"Extension: {imageInfo?.FileExtension}\n" +
                                 $"Download Link: {imageInfo?.DownloadLink}";

                await _snsClient.PublishAsync(new PublishRequest
                {
                    TopicArn = _snsTopicArn,
                    Message = snsMessage
                });
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Error processing SQS record: {ex}");
            }
        }
    }

    string GetSSMParameter(string parameterName)
    {
        using var ssmClient = new AmazonSimpleSystemsManagementClient();
        var request = new GetParameterRequest
        {
            Name = parameterName,
            WithDecryption = true
        };
        try
        {
            return ssmClient.GetParameterAsync(request).Result.Parameter.Value;
        }
        catch (Exception exception)
        {
            throw new Exception($"Failed to get SSM parameter '{parameterName}'", exception);
        }
    }

    public class ImageInfo
    {
        public string Name { get; set; }
        public long Size { get; set; }
        public string FileExtension { get; set; }
        public string DownloadLink { get; set; }
    }
}
