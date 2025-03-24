using Amazon.Util;

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

var region = EC2InstanceMetadata.Region;
var az = EC2InstanceMetadata.AvailabilityZone;

app.MapGet("/", () => new
{
    Region = EC2InstanceMetadata.Region.SystemName,
    EC2InstanceMetadata.AvailabilityZone
});

app.Run("http://0.0.0.0:80");
