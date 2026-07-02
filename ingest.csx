#r "nuget: Azure.Storage.Blobs, 12.19.1"
#r "nuget: DotNetEnv, 3.2.0"

using Azure.Storage.Blobs;
using DotNetEnv;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

string? connectionString = null;
string? containerName = null;
string? baseFolder = null;
string? stateFilePath = null;

var indentedJson = new JsonSerializerOptions
{
    WriteIndented = true
};

try
{
    Env.Load();

    connectionString = Env.GetString("AZURE_STORAGE_CONNECTION_STRING");
    containerName = Env.GetString("AZURE_CONTAINER_NAME");
    baseFolder = Env.GetString("AZURE_BASE_FOLDER");
    stateFilePath = Env.GetString("STATE_FILE_PATH", "last_run_state.txt");

    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException("AZURE_STORAGE_CONNECTION_STRING is not set in .env file");
    }

    if (string.IsNullOrWhiteSpace(containerName))
    {
        throw new InvalidOperationException("AZURE_CONTAINER_NAME is not set in .env file");
    }

    if (string.IsNullOrWhiteSpace(baseFolder))
    {
        throw new InvalidOperationException("AZURE_BASE_FOLDER is not set in .env file");
    }

    Console.WriteLine("Configuration loaded:");
    Console.WriteLine($"  Container: {containerName}");
    Console.WriteLine($"  Base Folder: {baseFolder}");
    Console.WriteLine($"  State File: {stateFilePath}");

    var stateDirectory = Path.GetDirectoryName(stateFilePath);
    if (!string.IsNullOrWhiteSpace(stateDirectory) && !Directory.Exists(stateDirectory))
    {
        Directory.CreateDirectory(stateDirectory);
    }

    var lastRunTime = GetLastRunTime(stateFilePath);
    Console.WriteLine($"Last retrieved at: {lastRunTime:yyyy-MM-dd HH:mm:ss} UTC");

    var blobServiceClient = new BlobServiceClient(connectionString);
    var containerClient = blobServiceClient.GetBlobContainerClient(containerName);

    if (!await containerClient.ExistsAsync())
    {
        Console.WriteLine($"Container '{containerName}' does not exist!");
        return;
    }

    var maxBlobTime = lastRunTime;
    var newCount = 0;
    var errorCount = 0;

    var blobs = containerClient.GetBlobsAsync(prefix: $"{baseFolder}/");
    await foreach (var blob in blobs)
    {
        var success = false;

        if (blob.Name.EndsWith('/'))
        {
            continue;
        }

        var blobTime = ExtractDateTimeFromPath(blob.Name, baseFolder);
        if (blobTime == DateTime.MinValue)
        {
            Console.WriteLine($"Could not get date from: {blob.Name}");
            errorCount++;
            continue;
        }

        if (blobTime <= lastRunTime)
        {
            continue;
        }

        try
        {
            var blobClient = containerClient.GetBlobClient(blob.Name);
            var response = await blobClient.DownloadAsync();

            using var streamReader = new StreamReader(response.Value.Content);
            var content = await streamReader.ReadToEndAsync();
            var lines = content.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                try
                {
                    var message = JsonSerializer.Deserialize<IoTHubMessage>(line);
                    if (message is null)
                    {
                        continue;
                    }

                    Console.WriteLine($"Name: {blob.Name}, time {blobTime:yyyy-MM-dd HH:mm:ss} UTC");
                    Console.WriteLine($"Enqueued Time: {message.EnqueuedTimeUtc}");
                    Console.WriteLine($"Schema: {message.Properties?.IoTHubMessageSchema ?? "N/A"}");
                    Console.WriteLine($"Creation Time: {message.Properties?.IoTHubCreationTimeUtc ?? "N/A"}");
                    Console.WriteLine($"Device ID: {message.SystemProperties?.ConnectionDeviceId ?? "N/A"}");
                    Console.WriteLine($"Device Generation ID: {message.SystemProperties?.ConnectionDeviceGenerationId ?? "N/A"}");
                    Console.WriteLine($"Auth Method: {message.SystemProperties?.ConnectionAuthMethod ?? "N/A"}");
                    Console.WriteLine("Body:");
                    Console.WriteLine(JsonSerializer.Serialize(message.Body, indentedJson));
                    Console.WriteLine();

                    success = true;
                    newCount++;
                }
                catch (JsonException ex)
                {
                    Console.WriteLine($"Error deserializing line: {ex.Message}");
                    Console.WriteLine($"Line: {line}");
                    errorCount++;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error downloading blob {blob.Name}: {ex.Message}");
            errorCount++;
        }

        if (success && blobTime > maxBlobTime)
        {
            maxBlobTime = blobTime;
        }
    }

    try
    {
        if (maxBlobTime > lastRunTime)
        {
            Console.WriteLine($"\nUpdating last run time to: {maxBlobTime:yyyy-MM-dd HH:mm:ss}");
            File.WriteAllText(stateFilePath, maxBlobTime.ToString("yyyy-MM-dd HH:mm:ss"));
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error updating last run time: {ex.Message}");
    }

    Console.WriteLine("\n=== Summary ===");
    Console.WriteLine($"Retrieved: {newCount} message(s)");
    Console.WriteLine($"Errors: {errorCount}");
    Console.WriteLine($"Most recent blob: {maxBlobTime:yyyy-MM-dd HH:mm:ss} UTC");
    Console.WriteLine($"\nCompleted at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine(ex.StackTrace);
}

static DateTime GetLastRunTime(string path)
{
    if (File.Exists(path))
    {
        try
        {
            var content = File.ReadAllText(path);
            return DateTime.ParseExact(content, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error reading state file: {ex.Message}");
        }
    }

    return DateTime.UtcNow.AddYears(-1);
}

static DateTime ExtractDateTimeFromPath(string path, string folderPrefix)
{
    try
    {
        var relativePath = path.Substring(folderPrefix.Length + 1);
        var parts = relativePath.Split('/');

        // Expected structure: partition/year/month/day/hour/minute(file)
        if (parts.Length >= 6)
        {
            if (!int.TryParse(parts[1], out var year)) return DateTime.MinValue;
            if (!int.TryParse(parts[2], out var month)) return DateTime.MinValue;
            if (!int.TryParse(parts[3], out var day)) return DateTime.MinValue;
            if (!int.TryParse(parts[4], out var hour)) return DateTime.MinValue;
            if (!int.TryParse(parts[5], out var minute)) return DateTime.MinValue;

            return new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error parsing date from path '{path}': {ex.Message}");
    }

    return DateTime.MinValue;
}

public class IoTHubMessage
{
    public DateTime EnqueuedTimeUtc { get; set; }
    public Properties? Properties { get; set; }
    public SystemProperties? SystemProperties { get; set; }
    public JsonElement Body { get; set; }
}

public class Properties
{
    [JsonPropertyName("iothub-message-schema")]
    public string? IoTHubMessageSchema { get; set; }

    [JsonPropertyName("iothub-creation-time-utc")]
    public string? IoTHubCreationTimeUtc { get; set; }
}

public class SystemProperties
{
    [JsonPropertyName("connectionDeviceId")]
    public string? ConnectionDeviceId { get; set; }

    [JsonPropertyName("connectionAuthMethod")]
    public string? ConnectionAuthMethod { get; set; }

    [JsonPropertyName("connectionDeviceGenerationId")]
    public string? ConnectionDeviceGenerationId { get; set; }

    [JsonPropertyName("enqueuedTime")]
    public string? EnqueuedTime { get; set; }
}
