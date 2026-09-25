import * as Device from 'expo-device';
import * as Application from 'expo-application';
import { Platform } from 'react-native';

/**
 * Collects all available device telemetry from the current device.
 * Uses expo-device and expo-application for cross-platform compatibility.
 */
export async function collectDeviceTelemetry() {
  // Unique device ID — on Android uses androidId, on iOS uses identifierForVendor
  const deviceId =
    Platform.OS === 'android'
      ? Application.getAndroidId()
      : await Application.getIosIdForVendorAsync();

  // Build a mock hardware fingerprint from stable hardware properties
  const hardwareFingerprint = buildHardwareFingerprint();

  return {
    deviceId: deviceId ?? 'UNKNOWN_DEVICE_ID',
    deviceName: Device.deviceName ?? 'Unknown Device',
    osName: Device.osName ?? Platform.OS,
    osVersion: Device.osVersion ?? 'Unknown',
    manufacturer: Device.manufacturer ?? 'Unknown',
    modelName: Device.modelName ?? 'Unknown Model',
    deviceType: Device.deviceType,
    hardwareFingerprint,
    appVersion: Application.nativeApplicationVersion ?? '1.0.0',
    buildVersion: Application.nativeBuildVersion ?? '1',
    registeredAt: new Date().toISOString(),
  };
}

/**
 * Builds a deterministic mock hardware fingerprint from stable device properties.
 * In production this would use a native module for true TPM/Secure Enclave data.
 */
function buildHardwareFingerprint() {
  const components = [
    Device.modelName ?? '',
    Device.manufacturer ?? '',
    Device.osName ?? '',
    Device.osVersion ?? '',
    Device.totalMemory?.toString() ?? '',
    Device.supportedCpuArchitectures?.join(',') ?? '',
  ];
  // Simple deterministic hash simulation (replace with real crypto in production)
  const raw = components.join('|');
  let hash = 0;
  for (let i = 0; i < raw.length; i++) {
    hash = (Math.imul(31, hash) + raw.charCodeAt(i)) | 0;
  }
  return `FP-${Math.abs(hash).toString(16).toUpperCase().padStart(8, '0')}`;
}

/**
 * MOCK: Sends device telemetry to the backend for registration.
 * In production, replace with your actual API endpoint.
 * Returns: { success: boolean, status: 'PENDING_APPROVAL' | 'APPROVED' | 'REJECTED' }
 */
export async function registerDeviceWithBackend(telemetry) {
  console.log('[DeviceService] Registering device with telemetry:', telemetry);

  // Simulate a network call (2-second delay)
  await new Promise((resolve) => setTimeout(resolve, 2000));

  // Mock response — in production this comes from your backend
  return {
    success: true,
    status: 'PENDING_APPROVAL',
    deviceId: telemetry.deviceId,
    message: 'Your device has been submitted for approval by an administrator.',
    submittedAt: new Date().toISOString(),
  };
}

/**
 * MOCK: Sends an MFA approval/denial decision to the backend.
 * @param {string} requestId - The MFA request ID from the push notification.
 * @param {'APPROVE' | 'DENY'} decision
 */
export async function submitMfaDecision(requestId, decision) {
  console.log(`[DeviceService] Submitting MFA decision: ${decision} for requestId: ${requestId}`);

  // Simulate network call
  await new Promise((resolve) => setTimeout(resolve, 800));

  return { success: true, decision, requestId };
}
