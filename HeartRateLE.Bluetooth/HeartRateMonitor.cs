﻿using HeartRateLE.Bluetooth.Events;
using HeartRateLE.Bluetooth.Schema;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Security.Cryptography;

namespace HeartRateLE.Bluetooth
{
    public class HeartRateMonitor
    {
        private BluetoothLEDevice _heartRateDevice = null;
        private List<BluetoothAttribute> _serviceCollection = new List<BluetoothAttribute>();

        private BluetoothAttribute _heartRateMeasurementAttribute;
        private BluetoothAttribute _heartRateAttribute;
        private GattCharacteristic _heartRateMeasurementCharacteristic;

        /// <summary>
        /// Occurs when [connection status changed].
        /// </summary>
        public event EventHandler<Events.ConnectionStatusChangedEventArgs> ConnectionStatusChanged;
        /// <summary>
        /// Raises the <see cref="E:ConnectionStatusChanged" /> event.
        /// </summary>
        /// <param name="e">The <see cref="Events.ConnectionStatusChangedEventArgs"/> instance containing the event data.</param>
        protected virtual void OnConnectionStatusChanged(Events.ConnectionStatusChangedEventArgs e)
        {
            ConnectionStatusChanged?.Invoke(this, e);
        }

        /// <summary>
        /// Occurs when [value changed].
        /// </summary>
        public event EventHandler<Events.RateChangedEventArgs> RateChanged;
        /// <summary>
        /// Raises the <see cref="E:ValueChanged" /> event.
        /// </summary>
        /// <param name="e">The <see cref="Events.RateChangedEventArgs"/> instance containing the event data.</param>
        protected virtual void OnRateChanged(Events.RateChangedEventArgs e)
        {
            RateChanged?.Invoke(this, e);
        }

        public async Task<ConnectionResult> ConnectAsync(string deviceId)
        {
            _heartRateDevice = await BluetoothLEDevice.FromIdAsync(deviceId);
            if (_heartRateDevice == null)
            {
                return new Schema.ConnectionResult()
                {
                    IsConnected = false,
                    ErrorMessage = "Could not find specified heart rate device"
                };
            }

            if (!_heartRateDevice.DeviceInformation.Pairing.IsPaired)
            {
                _heartRateDevice = null;
                return new Schema.ConnectionResult()
                {
                    IsConnected = false,
                    ErrorMessage = "Heart rate device is not paired"
                };
            }

            // we should always monitor the connection status
            _heartRateDevice.ConnectionStatusChanged -= DeviceConnectionStatusChanged;
            _heartRateDevice.ConnectionStatusChanged += DeviceConnectionStatusChanged;

            var isReachable = await GetDeviceServicesAsync();
            if (!isReachable)
            {
                _heartRateDevice = null;
                return new Schema.ConnectionResult()
                {
                    IsConnected = false,
                    ErrorMessage = "Heart rate device is unreachable (i.e. out of range or shutoff)"
                };
            }

            CharacteristicResult characteristicResult;
            characteristicResult = await SetupHeartRateCharacteristic();
            if (!characteristicResult.IsSuccess)
                return new Schema.ConnectionResult()
                {
                    IsConnected = false,
                    ErrorMessage = characteristicResult.Message
                };


            // we could force propagation of event with connection status change, to run the callback for initial status
            DeviceConnectionStatusChanged(_heartRateDevice, null);

            return new Schema.ConnectionResult()
            {
                IsConnected = _heartRateDevice.ConnectionStatus == BluetoothConnectionStatus.Connected,
                Name = _heartRateDevice.Name
            };
        }

        private async Task<List<BluetoothAttribute>> GetServiceCharacteristicsAsync(BluetoothAttribute service)
        {
            IReadOnlyList<GattCharacteristic> characteristics = null;
            try
            {
                // Ensure we have access to the device.
                var accessStatus = await service.service.RequestAccessAsync();
                if (accessStatus == DeviceAccessStatus.Allowed)
                {
                    // BT_Code: Get all the child characteristics of a service. Use the cache mode to specify uncached characterstics only 
                    // and the new Async functions to get the characteristics of unpaired devices as well. 
                    var result = await service.service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                    if (result.Status == GattCommunicationStatus.Success)
                    {
                        characteristics = result.Characteristics;
                    }
                    else
                    {
                        characteristics = new List<GattCharacteristic>();
                    }
                }
                else
                {
                    // Not granted access
                    // On error, act as if there are no characteristics.
                    characteristics = new List<GattCharacteristic>();
                }
            }
            catch (Exception ex)
            {
                characteristics = new List<GattCharacteristic>();
            }

            var characteristicCollection = new List<BluetoothAttribute>();
            characteristicCollection.AddRange(characteristics.Select(a => new BluetoothAttribute(a)));
            return characteristicCollection;
        }

        private async Task<CharacteristicResult> SetupHeartRateCharacteristic()
        {
            _heartRateAttribute = _serviceCollection.Where(a => a.Name == "HeartRate").FirstOrDefault();
            if (_heartRateAttribute == null)
            {
                return new CharacteristicResult()
                {
                    IsSuccess = false,
                    Message = "Cannot find HeartRate service"
                };
            }

            var characteristics = await GetServiceCharacteristicsAsync(_heartRateAttribute);
            _heartRateMeasurementAttribute = characteristics.Where(a => a.Name == "HeartRateMeasurement").FirstOrDefault();
            if (_heartRateMeasurementAttribute == null)
            {
                return new CharacteristicResult()
                {
                    IsSuccess = false,
                    Message = "Cannot find HeartRateMeasurement characteristic"
                };
            }
            _heartRateMeasurementCharacteristic = _heartRateMeasurementAttribute.characteristic;


            // Get all the child descriptors of a characteristics. Use the cache mode to specify uncached descriptors only 
            // and the new Async functions to get the descriptors of unpaired devices as well. 
            var result = await _heartRateMeasurementCharacteristic.GetDescriptorsAsync(BluetoothCacheMode.Uncached);
            if (result.Status != GattCommunicationStatus.Success)
            {
                return new CharacteristicResult()
                {
                    IsSuccess = false,
                    Message = result.Status.ToString()
                };
            }

            if (_heartRateMeasurementCharacteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
            {
                var status = await _heartRateMeasurementCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
                if (status == GattCommunicationStatus.Success)
                    _heartRateMeasurementCharacteristic.ValueChanged += HeartRateValueChanged;

                return new CharacteristicResult()
                {
                    IsSuccess = status == GattCommunicationStatus.Success,
                    Message = status.ToString()
                };
            }
            else
            {
                return new CharacteristicResult()
                {
                    IsSuccess = false,
                    Message = "HeartRateMeasurement characteristic does not support notify"
                };

            }

        }

        private async Task<bool> GetDeviceServicesAsync()
        {
            // Note: BluetoothLEDevice.GattServices property will return an empty list for unpaired devices. For all uses we recommend using the GetGattServicesAsync method.
            // BT_Code: GetGattServicesAsync returns a list of all the supported services of the device (even if it's not paired to the system).
            // If the services supported by the device are expected to change during BT usage, subscribe to the GattServicesChanged event.
            GattDeviceServicesResult result = await _heartRateDevice.GetGattServicesAsync(BluetoothCacheMode.Uncached);

            if (result.Status == GattCommunicationStatus.Success)
            {
                _serviceCollection.AddRange(result.Services.Select(a => new BluetoothAttribute(a)));
                return true;
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// Disconnects the current BLE heart rate device.
        /// </summary>
        /// <returns></returns>
        public async Task DisconnectAsync()
        {
            if (_heartRateDevice != null)
            {
                if (_heartRateMeasurementCharacteristic != null)
                {
                    //NOTE: might want to do something here if the result is not successful
                    var result = await _heartRateMeasurementCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None);
                    if (_heartRateMeasurementCharacteristic.Service != null)
                        _heartRateMeasurementCharacteristic.Service.Dispose();
                    _heartRateMeasurementCharacteristic = null;
                }

                if (_heartRateMeasurementAttribute != null)
                {
                    if (_heartRateMeasurementAttribute.service != null)
                        _heartRateMeasurementAttribute.service.Dispose();
                    _heartRateMeasurementAttribute = null;
                }

                if (_heartRateAttribute != null)
                {
                    if (_heartRateAttribute.service != null)
                        _heartRateAttribute.service.Dispose();
                    _heartRateAttribute = null;
                }

                _serviceCollection = new List<BluetoothAttribute>();

                _heartRateDevice.Dispose();
                _heartRateDevice = null;

                DeviceConnectionStatusChanged(null, null);
            }
        }
        private void DeviceConnectionStatusChanged(BluetoothLEDevice sender, object args)
        {
            var result = new ConnectionStatusChangedEventArgs()
            {
                IsConnected = sender != null && (sender.ConnectionStatus == BluetoothConnectionStatus.Connected)
            };

            OnConnectionStatusChanged(result);
        }

        private void HeartRateValueChanged(GattCharacteristic sender, GattValueChangedEventArgs e)
        {
            byte[] data;
            CryptographicBuffer.CopyToByteArray(e.CharacteristicValue, out data);

            var args = new Events.RateChangedEventArgs()
            {
                BeatsPerMinute = Utilities.ParseHeartRateValue(data)
            };
            OnRateChanged(args);
        }

        /// <summary>
        /// Gets a value indicating whether this instance is connected.
        /// </summary>
        /// <value>
        ///   <c>true</c> if this instance is connected; otherwise, <c>false</c>.
        /// </value>
        public bool IsConnected
        {
            get { return _heartRateDevice != null ? _heartRateDevice.ConnectionStatus == BluetoothConnectionStatus.Connected : false; }
        }

        /// <summary>
        /// Gets the device information for the current BLE heart rate device.
        /// </summary>
        /// <returns></returns>
        public async Task<Schema.HeartRateDeviceInfo> GetDeviceInfoAsync()
        {
            if (_heartRateDevice != null && _heartRateDevice.ConnectionStatus == BluetoothConnectionStatus.Connected)
            {
                var deviceInfoService = _serviceCollection.Where(a => a.Name == "DeviceInformation").FirstOrDefault();
                var deviceInfocharacteristics = await GetServiceCharacteristicsAsync(deviceInfoService);

                var batteryService = _serviceCollection.Where(a => a.Name == "Battery").FirstOrDefault();
                var batteryCharacteristics = await GetServiceCharacteristicsAsync(batteryService);
                //byte battery = await _batteryParser.ReadAsync();

                return new Schema.HeartRateDeviceInfo()
                {
                    DeviceId = _heartRateDevice.DeviceId,
                    Name = _heartRateDevice.Name,
                    Firmware = await Utilities.ReadCharacteristicValueAsync(deviceInfocharacteristics, "FirmwareRevisionString"),
                    Hardware = await Utilities.ReadCharacteristicValueAsync(deviceInfocharacteristics, "HardwareRevisionString"),
                    Manufacturer = await Utilities.ReadCharacteristicValueAsync(deviceInfocharacteristics, "ManufacturerNameString"),
                    SerialNumber = await Utilities.ReadCharacteristicValueAsync(deviceInfocharacteristics, "SerialNumberString"),
                    ModelNumber = await Utilities.ReadCharacteristicValueAsync(deviceInfocharacteristics, "ModelNumberString"),
                    BatteryPercent = Convert.ToInt32(await Utilities.ReadCharacteristicValueAsync(batteryCharacteristics, "BatteryLevel"))
                };
            }
            else
            {
                return new Schema.HeartRateDeviceInfo();
            }
        }
    }
}