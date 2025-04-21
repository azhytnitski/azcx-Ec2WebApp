using System.ComponentModel.DataAnnotations;
using Amazon.RDS.Util;
using Amazon.Runtime.CredentialManagement;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.Util;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Get bucket name from environment variable
var bucketName = Environment.GetEnvironmentVariable("AWS_BUCKET_NAME");
if (string.IsNullOrEmpty(bucketName))
{
    throw new Exception("Environment variable 'AWS_BUCKET_NAME' is not set.");
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

// Get RDS connection details from environment variables
var rdsEndpoint = Environment.GetEnvironmentVariable("RDS_ENDPOINT");
var rdsDatabase = Environment.GetEnvironmentVariable("RDS_DATABASE");
var rdsUsername = Environment.GetEnvironmentVariable("RDS_USERNAME");
var rdsPassword = Environment.GetEnvironmentVariable("RDS_PASSWORD"); // Optional password

if (string.IsNullOrEmpty(rdsEndpoint) || string.IsNullOrEmpty(rdsDatabase) || string.IsNullOrEmpty(rdsUsername))
{
    throw new Exception("Environment variables 'RDS_ENDPOINT', 'RDS_DATABASE', and 'RDS_USERNAME' must be set.");
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

var app = builder.Build();

// Dependency injection for database context
using var scope = app.Services.CreateScope();
var dbContext = scope.ServiceProvider.GetRequiredService<ImageMetadataContext>();
dbContext.Database.EnsureCreated(); // Ensure the database and table exist

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

    return Results.Ok($"Image '{fileName}' uploaded successfully.");
}).DisableAntiforgery();

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
