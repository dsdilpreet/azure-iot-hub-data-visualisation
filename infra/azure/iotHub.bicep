targetScope = 'resourceGroup'

@description('Azure region for the IoT Hub.')
param location string

@description('Name of the IoT Hub.')
@minLength(3)
@maxLength(50)
param iotHubName string

@description('IoT Hub SKU.')
@allowed([
  'F1'
  'B1'
  'B2'
  'B3'
  'S1'
  'S2'
  'S3'
])
param iotHubSkuName string = 'F1'

@description('Number of IoT Hub units to provision.')
@minValue(1)
param iotHubSkuCapacity int = 1

@description('Name of the blob container used by IoT Hub routing.')
param storageContainerName string

@description('Name of the IoT Hub custom routing endpoint.')
param routingEndpointName string = 'storage-endpoint'

@description('Name of the IoT Hub route that sends device messages to storage.')
param routeName string = 'route-to-storage'

@description('Storage account connection string for the routing endpoint.')
@secure()
param storageAccountConnectionString string

@description('Blob endpoint URI for the storage account.')
param storageEndpointUri string

resource iotHub 'Microsoft.Devices/IotHubs@2023-06-30' = {
  name: iotHubName
  location: location
  sku: {
    name: iotHubSkuName
    capacity: iotHubSkuCapacity
  }
  properties: {
    publicNetworkAccess: 'Enabled'
    minTlsVersion: '1.2'
    enableFileUploadNotifications: false
    routing: {
      endpoints: {
        storageContainers: [
          {
            name: routingEndpointName
            connectionString: storageAccountConnectionString
            containerName: storageContainerName
            batchFrequencyInSeconds: 300
            maxChunkSizeInBytes: 314572800
            encoding: 'JSON'
            fileNameFormat: '{iothub}/{partition}/{YYYY}/{MM}/{DD}/{HH}/{mm}'
            resourceGroup: resourceGroup().name
            subscriptionId: subscription().subscriptionId
            endpointUri: storageEndpointUri
          }
        ]
      }
      fallbackRoute: {
        name: '$fallback'
        source: 'DeviceMessages'
        condition: 'true'
        endpointNames: [
          'events'
        ]
        isEnabled: true
      }
      routes: [
        {
          name: routeName
          source: 'DeviceMessages'
          condition: 'true'
          endpointNames: [
            routingEndpointName
          ]
          isEnabled: true
        }
      ]
    }
  }
}

output iotHubId string = iotHub.id
output iotHubName string = iotHub.name
output iotHubHostname string = iotHub.properties.hostName
