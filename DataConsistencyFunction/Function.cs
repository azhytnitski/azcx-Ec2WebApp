using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.RDS.Util;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Npgsql; 

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace DataConsistencyFunction;

public class Function
{
    // Fetch SSM parameters and initialize clients outside the handler for best practice

    private static readonly string _rdsEndpoint;
    private static readonly string _rdsDatabase;
    private static readonly string _rdsUsername;
    private static readonly string _rdsPassword;
    private static readonly string _s3BucketName;
    private static readonly NpgsqlConnection _dbConnection;
    private static readonly AmazonS3Client _s3Client = new AmazonS3Client();

    static Function()
    {
        Console.WriteLine("=== Lambda static initializer: BEGIN ===");

        // Fetch SSM parameters
        using var ssmClient = new AmazonSimpleSystemsManagementClient();

        Console.WriteLine("Fetching SSM parameter: /azcx/rds/endpoint");
        _rdsEndpoint = GetSSMParameter(ssmClient, "/azcx/rds/endpoint");
        Console.WriteLine($"Fetched /azcx/rds/endpoint: {_rdsEndpoint}");

        Console.WriteLine("Fetching SSM parameter: /azcx/rds/database");
        _rdsDatabase = GetSSMParameter(ssmClient, "/azcx/rds/database");
        Console.WriteLine($"Fetched /azcx/rds/database: {_rdsDatabase}");

        Console.WriteLine("Fetching SSM parameter: /azcx/rds/username");
        _rdsUsername = GetSSMParameter(ssmClient, "/azcx/rds/username");
        Console.WriteLine($"Fetched /azcx/rds/username: {_rdsUsername}");

        Console.WriteLine("Fetching SSM parameter: /azcx/rds/password");
        _rdsPassword = GetSSMParameter(ssmClient, "/azcx/rds/password");
        Console.WriteLine($"Fetched /azcx/rds/password: {(string.IsNullOrEmpty(_rdsPassword) ? "[empty or IAM auth]" : "[set, hidden]")}");

        Console.WriteLine("Fetching SSM parameter: /azcx/s3/bucket-name");
        _s3BucketName = GetSSMParameter(ssmClient, "/azcx/s3/bucket-name");
        Console.WriteLine($"Fetched /azcx/s3/bucket-name: {_s3BucketName}");

        // Build connection string
        var password = !string.IsNullOrEmpty(_rdsPassword)
            ? _rdsPassword
            : RDSAuthTokenGenerator.GenerateAuthToken(_rdsEndpoint, 5432, _rdsUsername);

        var connectionStringBuilder = new NpgsqlConnectionStringBuilder
        {
            Host = _rdsEndpoint,
            Port = 5432,
            Database = _rdsDatabase,
            Username = _rdsUsername,
            Password = password,
            SslMode = SslMode.Require
        };
        // Log connection string without password
        var safeConnectionString = new NpgsqlConnectionStringBuilder(connectionStringBuilder.ToString()) { Password = "*****" }.ToString();
        Console.WriteLine($"Constructed PostgreSQL connection string (password hidden): {safeConnectionString}");

        _dbConnection = new NpgsqlConnection(connectionStringBuilder.ToString());
        try
        {
            _dbConnection.Open();
            Console.WriteLine("Database connection opened successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Database connection failed: {ex}");
            throw;
        }

        Console.WriteLine("=== Lambda static initializer: END ===");
    }

    private static string GetSSMParameter(AmazonSimpleSystemsManagementClient ssmClient, string parameterName)
    {
        var request = new GetParameterRequest
        {
            Name = parameterName,
            WithDecryption = true
        };
        try
        {
            return ssmClient.GetParameterAsync(request).Result.Parameter.Value;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to get SSM parameter '{parameterName}': {ex}");
            throw new Exception($"Failed to get SSM parameter '{parameterName}'", ex);
        }
    }

    public async Task<LambdaResponse> FunctionHandler(Stream inputStream, ILambdaContext context)
    {
        // Read input and deserialize to LambdaInput
        string detailType;
        using (var reader = new StreamReader(inputStream))
        {
            detailType = JsonSerializer.Deserialize<LambdaInput>(await reader.ReadToEndAsync())?.DetailType ?? "Unknown";
        }

        context.Logger.LogLine($"Invocation source: {detailType}");

        // 1. Get image metadata from DB (table 'ImageMetadata' with 'Name' column)
        List<string> dbImageNames = new List<string>();
        using (var cmd = new NpgsqlCommand("SELECT \"Name\" FROM \"ImageMetadata\"", _dbConnection))
        using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                dbImageNames.Add(reader.GetString(0));
            }
        }

        // 2. Get image keys from S3 bucket
        List<string> s3ImageNames = new List<string>();
        string? continuationToken = null;
        do
        {
            var request = new ListObjectsV2Request
            {
                BucketName = _s3BucketName,
                ContinuationToken = continuationToken
            };
            var response = await _s3Client.ListObjectsV2Async(request);
            s3ImageNames.AddRange(response.S3Objects.Select(o => o.Key).Where(k => !k.StartsWith("app")));
            continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
        } while (continuationToken != null);

        // 3. Simple validation: every DB image name must exist in S3
        var missingInS3 = dbImageNames.Except(s3ImageNames).ToList();
        var extraInS3 = s3ImageNames.Except(dbImageNames).ToList();
        bool isConsistent = !missingInS3.Any() && !extraInS3.Any();

        // 4. Log and return result
        var result = new
        {
            Consistent = isConsistent,
            MissingInS3 = missingInS3,
            ExtraInS3 = extraInS3,
            InvocationSource = detailType
        };
        context.Logger.LogLine($"Validation result: {JsonSerializer.Serialize(result)}");

        return new LambdaResponse
        {
            StatusCode = 200,
            Body = JsonSerializer.Serialize(result)
        };
    }
}

public class LambdaInput
{
    public string? DetailType { get; set; }
}

public class LambdaResponse
{
    public int StatusCode { get; set; }
    public string Body { get; set; }
}
