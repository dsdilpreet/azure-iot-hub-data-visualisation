using Azure.Storage.Blobs;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotNetEnv;

class Program
{
    private static string? ConnectionString;
    private static string? ContainerName;
    private static string? BaseFolder;
    private static string? StateFilePath;

    static async Task Main(string[] args)
    {
        try
        {
            Env.Load();

            ConnectionString = Env.GetString("AZURE_STORAGE_CONNECTION_STRING");
            ContainerName = Env.GetString("AZURE_CONTAINER_NAME");
            BaseFolder = Env.GetString("AZURE_BASE_FOLDER");
            StateFilePath = Env.GetString("STATE_FILE_PATH", "last_run_state.txt");

            if (string.IsNullOrEmpty(ConnectionString))
                throw new InvalidOperationException("AZURE_STORAGE_CONNECTION_STRING is not set in .env file");
            if (string.IsNullOrEmpty(ContainerName))
                throw new InvalidOperationException("AZURE_CONTAINER_NAME is not set in .env file");
            if (string.IsNullOrEmpty(BaseFolder))
                throw new InvalidOperationException("AZURE_BASE_FOLDER is not set in .env file");

            Console.WriteLine($"Configuration loaded:");
            Console.WriteLine($"  Container: {ContainerName}");
            Console.WriteLine($"  Base Folder: {BaseFolder}");
            Console.WriteLine($"  State File: {StateFilePath}");

            string? stateDirectory = Path.GetDirectoryName(StateFilePath);
            if (!string.IsNullOrEmpty(stateDirectory) && !Directory.Exists(stateDirectory))
            {
                Directory.CreateDirectory(stateDirectory);
            }

            // Get the last download timestamp
            DateTime lastRunTime = GetLastRunTime();
            Console.WriteLine($"Last retrieved at: {lastRunTime:yyyy-MM-dd HH:mm:ss} UTC");

            BlobServiceClient blobServiceClient = new BlobServiceClient(ConnectionString);
            BlobContainerClient containerClient = blobServiceClient.GetBlobContainerClient(ContainerName);

            if (!await containerClient.ExistsAsync())
            {
                Console.WriteLine($"Container '{ContainerName}' does not exist!");
                return;
            }

            DateTime maxBlobTime = lastRunTime;
            int newCount = 0;
            int errorCount = 0;
            var success = false;

            var blobs = containerClient.GetBlobsAsync(prefix: $"{BaseFolder}/");
            await foreach (var blob in blobs)
            {
                success = false;

                if (blob.Name.EndsWith("/"))
                    continue;

                DateTime blobTime = ExtractDateTimeFromPath(blob.Name);

                if (blobTime == DateTime.MinValue)
                {
                    Console.WriteLine($"Could not get date from: {blob.Name}");
                    errorCount++;
                    continue;
                }

                // Check if this blob is newer than the last run time
                if (blobTime > lastRunTime)
                {
                    //Console.WriteLine($"New blob: {blob.Name} with time {blobTime:yyyy-MM-dd HH:mm:ss} UTC");

                    // Download and deserialize blob
                    try
                    {
                        BlobClient blobClient = containerClient.GetBlobClient(blob.Name);
                        var response = await blobClient.DownloadAsync();

                        using (var streamReader = new StreamReader(response.Value.Content))
                        {
                            string content = await streamReader.ReadToEndAsync();

                            // The blob contains multiple JSON objects (one per line)
                            var lines = content.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

                            foreach (var line in lines)
                            {
                                try
                                {
                                    var message = JsonSerializer.Deserialize<IoTHubMessage>(line);
                                    if (message != null)
                                    {
                                        Console.WriteLine($"Name: {blob.Name}, time {blobTime:yyyy-MM-dd HH:mm:ss} UTC");
                                        Console.WriteLine($"Enqueued Time: {message.EnqueuedTimeUtc}");
                                        Console.WriteLine($"Schema: {message.Properties?.IoTHubMessageSchema ?? "N/A"}");
                                        Console.WriteLine($"Creation Time: {message.Properties?.IoTHubCreationTimeUtc ?? "N/A"}");
                                        Console.WriteLine($"Device ID: {message.SystemProperties?.ConnectionDeviceId ?? "N/A"}");
                                        Console.WriteLine($"Device Generation ID: {message.SystemProperties?.ConnectionDeviceGenerationId ?? "N/A"}");
                                        Console.WriteLine($"Auth Method: {message.SystemProperties?.ConnectionAuthMethod ?? "N/A"}");
                                        Console.WriteLine($"Body:");
                                        Console.WriteLine(JsonSerializer.Serialize(message.Body, IndentedJson));
                                        Console.WriteLine();
                                    }
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
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error downloading blob {blob.Name}: {ex.Message}");
                        errorCount++;
                    }

                    // Update max time found
                    if (success && blobTime > maxBlobTime)
                    {
                        maxBlobTime = blobTime;
                    }
                }

            }

            // Update the last run time
            try
            {
                if (maxBlobTime > lastRunTime) // only update runtime if there are blobs with no errors
                {
                    Console.WriteLine($"\nUpdating last run time to: {maxBlobTime}");
                    File.WriteAllText(StateFilePath, maxBlobTime.ToString("yyyy-MM-dd HH:mm:ss"));

                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error updating last run time: {ex.Message}");
            }

            // Summary
            Console.WriteLine($"\n=== Summary ===");
            Console.WriteLine($"Retrieved: {newCount} message(s)");
            Console.WriteLine($"Errors: {errorCount}");
            Console.WriteLine($"Most recent blob: {maxBlobTime}");
            Console.WriteLine($"\nCompleted at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");

        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }

    static DateTime GetLastRunTime()
    {
        if (File.Exists(StateFilePath))
        {
            try
            {
                string content = File.ReadAllText(StateFilePath);
                return DateTime.ParseExact(content, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading state file: {ex.Message}");
            }
        }

        // If state file doesn't exist, get all blobs from the past year
        return DateTime.UtcNow.AddYears(-1);
    }

    static DateTime ExtractDateTimeFromPath(string path)
    {
        try
        {
            string relativePath = path.Substring(BaseFolder.Length + 1);
            string[] parts = relativePath.Split('/');

            //Console.WriteLine($"Full path: {path}");
            //Console.WriteLine($"Relative path: {relativePath}");

            // Structure: partition/year/month/day/hour/minute(file)
            if (parts.Length >= 6)
            {
                if (!int.TryParse(parts[1], out int year)) return DateTime.MinValue;
                if (!int.TryParse(parts[2], out int month)) return DateTime.MinValue;
                if (!int.TryParse(parts[3], out int day)) return DateTime.MinValue;
                if (!int.TryParse(parts[4], out int hour)) return DateTime.MinValue;
                if (!int.TryParse(parts[5], out int minute)) return DateTime.MinValue;

                return new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing date from path '{path}': {ex.Message}");
        }

        return DateTime.MinValue;
    }

    private static readonly JsonSerializerOptions IndentedJson =
        new()
        {
            WriteIndented = true
        };
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

    // Ignoring custom properties for now
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

    // Ignoring custom properties for now
}