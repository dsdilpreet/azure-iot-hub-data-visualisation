targetScope = 'subscription'

@description('Name of the resource group to create and deploy resources into.')
param resourceGroupName string

@description('Azure region for the resource group and contained resources.')
param location string

@description('Name of the IoT Hub.')
param iotHubName string

@description('IoT Hub SKU.')
param iotHubSkuName string = 'F1'

@description('Globally unique name for the storage account.')
param storageAccountName string

@description('Name of the blob container used by IoT Hub routing.')
param storageContainerName string

resource resourceGroup 'Microsoft.Resources/resourceGroups@2024-11-01' = {
  name: resourceGroupName
  location: location
}

module storage './storage.bicep' = {
	scope: resourceGroup
	params: {
		location: location
		storageAccountName: storageAccountName
		storageContainerName: storageContainerName
	}
}

module iotHub './iotHub.bicep' = {
	scope: resourceGroup
	params: {
		location: location
		iotHubName: iotHubName
		iotHubSkuName: iotHubSkuName
		storageContainerName: storage.outputs.storageContainerName
		storageAccountConnectionString: storage.outputs.storageAccountConnectionString
		storageEndpointUri: storage.outputs.storageEndpointUri
	}
}

output resourceGroupId string = resourceGroup.id
output resourceGroupResourceName string = resourceGroup.name
output storageAccountId string = storage.outputs.storageAccountId
output storageAccountResourceName string = storage.outputs.storageAccountName
output storageContainerResourceName string = storage.outputs.storageContainerName
output iotHubId string = iotHub.outputs.iotHubId
output iotHubHostname string = iotHub.outputs.iotHubHostname
