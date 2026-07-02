using './main.bicep'

param resourceGroupName = 'rg-iot-hub-data-vis-dev'
param location = 'uksouth'
param iotHubName = 'iot-hub-data-vis-dev'
param iotHubSkuName = 'F1'
param storageAccountName = 'iothubdatavisdev001'
param storageContainerName = 'iot-messages'
