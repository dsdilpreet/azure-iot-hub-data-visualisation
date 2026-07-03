#nullable enable

#r "nuget: Azure.Storage.Blobs, 12.19.1"
#r "nuget: DotNetEnv, 3.2.0"
#r "nuget: Npgsql, 8.0.4"

using Azure.Storage.Blobs;
using DotNetEnv;
using Npgsql;
using NpgsqlTypes;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

string? connectionString = null;
string? containerName = null;
string? iotHubName = null;
string? stateFilePath = null;
string? dbHost = null;
int dbPort = 5432;
string? dbName = null;
string? dbUser = null;
string? dbPassword = null;

try
{
    Env.Load();

    connectionString = Env.GetString("AZURE_STORAGE_CONNECTION_STRING");
    containerName = Env.GetString("AZURE_STORAGE_CONTAINER_NAME");
    iotHubName = Env.GetString("AZURE_IOT_HUB_NAME");
    stateFilePath = Env.GetString("STATE_FILE_PATH", "last_run_state.txt");
    dbHost = Env.GetString("DB_HOST", "localhost");
    dbPort = int.TryParse(Env.GetString("DB_PORT", "5432"), out var parsedPort) ? parsedPort : 5432;
    dbName = Env.GetString("DB_NAME", "observability");
    dbUser = Env.GetString("DB_USER", "postgres");
    dbPassword = Env.GetString("DB_PASSWORD", "postgres");

    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException("AZURE_STORAGE_CONNECTION_STRING is not set in .env file");
    }

    if (string.IsNullOrWhiteSpace(containerName))
    {
        throw new InvalidOperationException("AZURE_STORAGE_CONTAINER_NAME is not set in .env file");
    }

    if (string.IsNullOrWhiteSpace(iotHubName))
    {
        throw new InvalidOperationException("AZURE_IOT_HUB_NAME is not set in .env file");
    }

    Console.WriteLine("Configuration loaded:");
    Console.WriteLine($"  Container: {containerName}");
    Console.WriteLine($"  IoT Hub Name: {iotHubName}");
    Console.WriteLine($"  State File: {stateFilePath}");
    Console.WriteLine($"  Database: {dbHost}:{dbPort}/{dbName}");

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
    var insertedCount = 0;
    var dbErrorCount = 0;

    var dbConnectionString = new NpgsqlConnectionStringBuilder
    {
        Host = dbHost,
        Port = dbPort,
        Database = dbName,
        Username = dbUser,
        Password = dbPassword,
        SslMode = SslMode.Disable
    }.ConnectionString;

    await using var dbConnection = new NpgsqlConnection(dbConnectionString);
    await dbConnection.OpenAsync();
    Console.WriteLine("Connected to PostgreSQL.");

    const string insertSql = @"
        INSERT INTO iot_messages (
            enqueued_time,
            iothub_creation_time,
            iothub_message_schema,
            connection_device_id,
            body,
            properties,
            system_properties
        )
        VALUES (
            @enqueued_time,
            @iothub_creation_time,
            @iothub_message_schema,
            @connection_device_id,
            @body,
            @properties,
            @system_properties
        );";

    await using var insertCommand = new NpgsqlCommand(insertSql, dbConnection);
    insertCommand.Parameters.Add(new NpgsqlParameter("enqueued_time", NpgsqlDbType.TimestampTz));
    insertCommand.Parameters.Add(new NpgsqlParameter("iothub_creation_time", NpgsqlDbType.TimestampTz));
    insertCommand.Parameters.Add(new NpgsqlParameter("iothub_message_schema", NpgsqlDbType.Text));
    insertCommand.Parameters.Add(new NpgsqlParameter("connection_device_id", NpgsqlDbType.Text));
    insertCommand.Parameters.Add(new NpgsqlParameter("body", NpgsqlDbType.Jsonb));
    insertCommand.Parameters.Add(new NpgsqlParameter("properties", NpgsqlDbType.Jsonb));
    insertCommand.Parameters.Add(new NpgsqlParameter("system_properties", NpgsqlDbType.Jsonb));

    var blobs = containerClient.GetBlobsAsync(prefix: $"{iotHubName}/");
    await foreach (var blob in blobs)
    {
        var success = false;

        if (blob.Name.EndsWith('/'))
        {
            continue;
        }

        var blobTime = ExtractDateTimeFromPath(blob.Name, iotHubName);
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
                    var message = JsonSerializer.Deserialize<IotHubMessage>(line);
                    if (message is null)
                    {
                        continue;
                    }

                    try
                    {
                        var enqueuedTime = DateTime.SpecifyKind(message.EnqueuedTimeUtc, DateTimeKind.Utc);

                        insertCommand.Parameters["enqueued_time"].Value = enqueuedTime;
                        insertCommand.Parameters["iothub_creation_time"].Value = GetIotHubCreationTime(message) ?? (object)DBNull.Value;
                        insertCommand.Parameters["iothub_message_schema"].Value = GetIotHubMessageSchema(message) ?? (object)DBNull.Value;
                        insertCommand.Parameters["connection_device_id"].Value = GetConnectionDeviceId(message) ?? (object)DBNull.Value;
                        insertCommand.Parameters["body"].Value = GetJsonOrDbNull(message.Body);
                        insertCommand.Parameters["properties"].Value = GetJsonOrDbNull(message.Properties);
                        insertCommand.Parameters["system_properties"].Value = GetJsonOrDbNull(message.SystemProperties);

                        await insertCommand.ExecuteNonQueryAsync();
                        insertedCount++;

                        if (insertedCount % 500 == 0)
                        {
                            Console.WriteLine($"Progress: inserted {insertedCount} messages so far...");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error inserting message into database: {ex.Message}");
                        dbErrorCount++;
                        continue;
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
    Console.WriteLine($"Inserted: {insertedCount} message(s)");
    Console.WriteLine($"Database errors: {dbErrorCount}");
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

    return DateTime.UtcNow.AddDays(-7);
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

static DateTime? GetIotHubCreationTime(IotHubMessage message)
{
    if (!TryGetPropertyIgnoreCase(message.Properties, "iothub-creation-time-utc", out var creationTimeElement))
    {
        return null;
    }

    var creationTimeRaw = creationTimeElement.GetString();
    if (string.IsNullOrWhiteSpace(creationTimeRaw))
    {
        return null;
    }

    if (DateTime.TryParse(
        creationTimeRaw,
        CultureInfo.InvariantCulture,
        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
        out var creationTime))
    {
        return creationTime;
    }

    return null;
}

static string? GetIotHubMessageSchema(IotHubMessage message)
{
    if (!TryGetPropertyIgnoreCase(message.Properties, "iothub-message-schema", out var schemaElement))
    {
        return null;
    }

    return schemaElement.GetString();
}

static string? GetConnectionDeviceId(IotHubMessage message)
{
    if (!TryGetPropertyIgnoreCase(message.SystemProperties, "connectionDeviceId", out var deviceIdElement))
    {
        return null;
    }

    return deviceIdElement.GetString();
}

static object GetJsonOrDbNull(JsonElement element)
{
    if (element.ValueKind == JsonValueKind.Undefined)
    {
        return DBNull.Value;
    }

    return element.GetRawText();
}

static bool TryGetPropertyIgnoreCase(JsonElement jsonObject, string propertyName, out JsonElement value)
{
    foreach (var property in jsonObject.EnumerateObject())
    {
        if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
        {
            value = property.Value;
            return true;
        }
    }

    value = default;
    return false;
}

public class IotHubMessage
{
    public DateTime EnqueuedTimeUtc { get; set; }
    public JsonElement Properties { get; set; }
    public JsonElement SystemProperties { get; set; }
    public JsonElement Body { get; set; }
}
