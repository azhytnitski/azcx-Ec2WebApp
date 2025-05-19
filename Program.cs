using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Amazon.RDS.Util;
using Amazon.Runtime.CredentialManagement;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Amazon.Util;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Helper function to fetch SSM parameter
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

// Get bucket name from SSM parameter
var bucketName = GetSSMParameter("/azcx/s3/bucket-name");
if (string.IsNullOrEmpty(bucketName))
{
    throw new Exception("SSM parameter '/azcx/s3/bucket-name' is not set.");
}

// Configure AWS S3 client
AmazonS3Client s3Client;
var profileName = Environment.GetEnvironmentVariable("AWS_PROFILE_NAME"); // Get profile name from environment variable

if (!string.IsNullOrEmpty(profileName))
{
    // Use AWS CLI profile if the environment variable is set
    var sharedFile = new SharedCredentialsFile();
    if (sharedFile.TryGetProfile(profileName, out var profile) &&
        AWSCredentialsFactory.TryGetAWSCredentials(profile, sharedFile, out var credentials))
    {
        s3Client = new AmazonS3Client(credentials, Amazon.RegionEndpoint.USEast1);
    }
    else
    {
        throw new Exception($"AWS profile '{profileName}' not found.");
    }
}
else
{
    // Use IAM role if the environment variable is not set
    s3Client = new AmazonS3Client();
}

// Get RDS connection details from SSM parameters
var rdsEndpoint = GetSSMParameter("/azcx/rds/endpoint");
var rdsDatabase = GetSSMParameter("/azcx/rds/database");
var rdsUsername = GetSSMParameter("/azcx/rds/username");
var rdsPassword = GetSSMParameter("/azcx/rds/password"); // Optional password

if (string.IsNullOrEmpty(rdsEndpoint) || string.IsNullOrEmpty(rdsDatabase) || string.IsNullOrEmpty(rdsUsername))
{
    throw new Exception("SSM parameters '/azcx/rds/endpoint', '/azcx/rds/database', and '/azcx/rds/username' must be set.");
}

// Generate an IAM authentication token for PostgreSQL if no password is provided
string GetRdsPassword()
{
    if (!string.IsNullOrEmpty(rdsPassword))
    {
        return rdsPassword; // Use the provided password
    }

    // Generate IAM authentication token
    return RDSAuthTokenGenerator.GenerateAuthToken(
        rdsEndpoint,
        5432,
        rdsUsername);
}

// Configure PostgreSQL database context
builder.Services.AddDbContext<ImageMetadataContext>(options =>
{
    var password = GetRdsPassword();
    var connectionString = new NpgsqlConnectionStringBuilder
    {
        Host = rdsEndpoint,
        Port = 5432,
        Database = rdsDatabase,
        Username = rdsUsername,
        Password = password,
        SslMode = SslMode.Require
    }.ToString();

    options.UseNpgsql(connectionString);
});

// Configure AWS SNS and SQS clients
var snsClient = new AmazonSimpleNotificationServiceClient();
var sqsClient = new AmazonSQSClient();

// Get SSM parameters for SNS topic and SQS queue
var snsTopicArn = GetSSMParameter("/azcx/sns/uploads-notification-topic-arn");
var sqsQueueUrl = GetSSMParameter("/azcx/sqs/uploads-notification-queue-url");

var app = builder.Build();

// Dependency injection for database context
using var scope = app.Services.CreateScope();
var dbContext = scope.ServiceProvider.GetRequiredService<ImageMetadataContext>();
dbContext.Database.EnsureCreated(); // Ensure the database and table exist

// Subscribe an email for notifications
app.MapPost("/subscribe", async ([FromBody] string email) =>
{
    var request = new SubscribeRequest
    {
        Protocol = "email",
        Endpoint = email,
        TopicArn = snsTopicArn
    };
    await snsClient.SubscribeAsync(request);
    return Results.Ok($"Subscription request sent to {email}. Please confirm the subscription.");
}).DisableAntiforgery(); 

// Unsubscribe an email from notifications
app.MapPost("/unsubscribe", async ([FromBody] string email) =>
{
    var subscriptions = await snsClient.ListSubscriptionsByTopicAsync(snsTopicArn);
    var subscription = subscriptions.Subscriptions.FirstOrDefault(s => s.Endpoint == email);
    if (subscription == null)
    {
        return Results.NotFound($"No subscription found for {email}.");
    }

    await snsClient.UnsubscribeAsync(subscription.SubscriptionArn);
    return Results.Ok($"Unsubscription request sent for {email}.");
}).DisableAntiforgery();

// Download an image by name
app.MapGet("/images/{name}", async (string name) =>
{
    try
    {
        var response = await s3Client.GetObjectAsync(bucketName, name);
        return Results.File(response.ResponseStream, response.Headers["Content-Type"], name);
    }
    catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        return Results.NotFound($"Image '{name}' not found.");
    }
});

// Show metadata for an existing image by name
app.MapGet("/images/{name}/metadata", async (string name, ImageMetadataContext db) =>
{
    var metadata = await db.ImageMetadata.FindAsync(name);
    if (metadata == null)
    {
        return Results.NotFound($"Metadata for image '{name}' not found.");
    }

    return Results.Ok(metadata);
});

// Show metadata for a random image
app.MapGet("/images/random/metadata", async (ImageMetadataContext db) =>
{
    var count = await db.ImageMetadata.CountAsync();
    if (count == 0)
    {
        return Results.NotFound("No images available.");
    }

    var randomIndex = new Random().Next(count);
    var randomImage = await db.ImageMetadata.Skip(randomIndex).FirstOrDefaultAsync();

    return Results.Ok(randomImage);
});

// Upload an image
app.MapPost("/images", async (IFormFile file, ImageMetadataContext db) =>
{
    if (file.Length == 0)
    {
        return Results.BadRequest("File is empty.");
    }

    var fileName = file.FileName;
    var fileExtension = Path.GetExtension(fileName).TrimStart('.').ToLower();

    using var memoryStream = new MemoryStream();
    await file.CopyToAsync(memoryStream);

    // Upload to S3
    var putRequest = new PutObjectRequest
    {
        BucketName = bucketName,
        Key = fileName,
        InputStream = memoryStream,
        ContentType = file.ContentType
    };
    await s3Client.PutObjectAsync(putRequest);

    // Store metadata in PostgreSQL
    var metadata = new ImageMetadata
    {
        Name = fileName,
        LastUpdated = DateTime.UtcNow,
        FileExtension = fileExtension,
        Size = file.Length
    };

    db.ImageMetadata.Add(metadata);
    await db.SaveChangesAsync();

    // Publish a message to the SQS queue
    var message = new ImageInfo
    {
        Name = fileName,
        Size = file.Length,
        FileExtension = fileExtension,
        DownloadLink = $"http://{EC2InstanceMetadata.NetworkInterfaces.First().PublicIPv4s.First()}/images/{Uri.EscapeDataString(fileName)}"
    };
    var sendMessageRequest = new SendMessageRequest
    {
        QueueUrl = sqsQueueUrl,
        MessageBody = JsonSerializer.Serialize(message)
    };
    await sqsClient.SendMessageAsync(sendMessageRequest);

    return Results.Ok($"Image '{fileName}' uploaded successfully.");
}).DisableAntiforgery();

// Background process to process SQS messages and send them to SNS
var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
_ = Task.Run(async () =>
{
    while (await timer.WaitForNextTickAsync())
    {
        try
        {
            Console.WriteLine("[SQS] Attempting to receive messages from SQS queue...");
            var receiveMessageRequest = new ReceiveMessageRequest
            {
                QueueUrl = sqsQueueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 5
            };
            var response = await sqsClient.ReceiveMessageAsync(receiveMessageRequest);
            Console.WriteLine($"[SQS] Received {response.Messages?.Count ?? 0} message(s) from SQS queue.");

            foreach (var message in response.Messages ?? Enumerable.Empty<Message>())
            {
                Console.WriteLine($"[SQS] Processing message with ID: {message.MessageId}");


                var imageInfo = JsonSerializer.Deserialize<ImageInfo>(message.Body);
                var snsMessage = $"An image has been uploaded:\n\n" +
                 $"Name: {imageInfo.Name}\n" +
                 $"Size: {imageInfo.Size} bytes\n" +
                 $"Extension: {imageInfo.FileExtension}\n" +
                 $"Download Link: {imageInfo.DownloadLink}";
                Console.WriteLine($"[SNS] Publishing message to SNS topic: {snsTopicArn}\n[SNS] Message: {snsMessage}");
                var publishResponse = await snsClient.PublishAsync(new PublishRequest
                {
                    TopicArn = snsTopicArn,
                    Message = snsMessage
                });
                Console.WriteLine($"[SNS] Published message to SNS. MessageId: {publishResponse.MessageId}");

                Console.WriteLine($"[SQS] Deleting message with ID: {message.MessageId} from SQS queue...");
                await sqsClient.DeleteMessageAsync(sqsQueueUrl, message.ReceiptHandle);
                Console.WriteLine($"[SQS] Deleted message with ID: {message.MessageId} from SQS queue.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SQS] Exception occurred during SQS message processing: {ex}");
        }
    }
});

// Delete an image by name
app.MapDelete("/images/{name}", async (string name, ImageMetadataContext db) =>
{
    try
    {
        await s3Client.DeleteObjectAsync(bucketName, name);

        var metadata = await db.ImageMetadata.FindAsync(name);
        if (metadata != null)
        {
            db.ImageMetadata.Remove(metadata);
            await db.SaveChangesAsync();
        }

        return Results.Ok($"Image '{name}' deleted successfully.");
    }
    catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        return Results.NotFound($"Image '{name}' not found.");
    }
});

// Default route
app.MapGet("/", () => new
{
    Region = EC2InstanceMetadata.Region.SystemName,
    EC2InstanceMetadata.AvailabilityZone
});

app.Run("http://0.0.0.0:80");

// Database context for PostgreSQL
public class ImageMetadataContext : DbContext
{
    public ImageMetadataContext(DbContextOptions<ImageMetadataContext> options) : base(options) { }

    public DbSet<ImageMetadata> ImageMetadata { get; set; }
}

// Entity model for image metadata
public class ImageMetadata
{
    [Key] // Mark 'Name' as the primary key
    public string Name { get; set; }
    public DateTime LastUpdated { get; set; }
    public string FileExtension { get; set; }
    public long Size { get; set; }
}

public class ImageInfo
{
    public string Name { get; set; }
    public long Size { get; set; }
    public string FileExtension { get; set; }
    public string DownloadLink { get; set; }
}
