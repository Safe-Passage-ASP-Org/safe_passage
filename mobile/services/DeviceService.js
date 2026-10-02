import * as Device from 'expo-device';
import * as Application from 'expo-application';
import { Platform } from 'react-native';
import { getToken } from './AuthService';
import { BASE_URL } from '../config';

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

  const os = `${Device.osName || (Platform.OS === 'android' ? 'Android' : 'iOS')} ${Device.osVersion || ''}`.trim();
  const deviceName = Device.deviceName ?? `${Device.manufacturer || ''} ${Device.modelName || 'Device'}`.trim() || 'Android Device';

  return {
    deviceId: deviceId || 'DEVICE-' + Math.random().toString(36).substring(2, 9).toUpperCase(),
    deviceName,
    deviceType: 'Phone',
    os,
    osName: Device.osName ?? (Platform.OS === 'android' ? 'Android' : 'iOS'),
    osVersion: Device.osVersion ?? 'Unknown',
    manufacturer: Device.manufacturer ?? 'Unknown',
    modelName: Device.modelName ?? 'Unknown Model',
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
 * Sends device telemetry to the backend for registration.
 * @param {object} telemetry - The collected device telemetry object.
 * @returns {Promise<{success: boolean, status: string, deviceId: string, message: string}>}
 */
export async function registerDeviceWithBackend(telemetry) {
  console.log('[DeviceService] Registering device with telemetry:', telemetry);

  const token = await getToken();

  const payload = {
    deviceId: telemetry.deviceId,
    deviceName: telemetry.deviceName,
    deviceType: telemetry.deviceType || 'Phone',
    os: telemetry.os || `${telemetry.osName} ${telemetry.osVersion}`.trim(),
  };

  const response = await fetch(`${BASE_URL}/api/devices/register`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
    },
    body: JSON.stringify(payload),
  });

  if (!response.ok) {
    const err = await response.json().catch(() => ({}));
    throw new Error(err.detail ?? err.title ?? response.statusText ?? 'Registration failed.');
  }

  const data = await response.json().catch(() => ({}));
  return {
    success: true,
    status: data.status === 'PendingApproval' ? 'PENDING_APPROVAL' : (data.status || 'PENDING_APPROVAL'),
    deviceId: data.deviceId ?? telemetry.deviceId,
    message: 'Your device has been submitted for administrator approval.',
    submittedAt: data.registrationDate ?? new Date().toISOString(),
    raw: data,
  };
}

/**
 * Sends an MFA approval or denial decision to the backend.
 * @param {string} requestId - The MFA request ID from the push notification.
 * @param {'APPROVE' | 'DENY'} decision
 */
export async function submitMfaDecision(requestId, decision) {
  console.log(`[DeviceService] Submitting MFA decision: ${decision} for requestId: ${requestId}`);

  const token = await getToken();
  const headers = {
    'Content-Type': 'application/json',
    ...(token ? { Authorization: `Bearer ${token}` } : {}),
  };

  const endpoint =
    decision === 'APPROVE'
      ? `${BASE_URL}/api/auth/mfa/approve`
      : `${BASE_URL}/api/auth/mfa/deny`;

  const body =
    decision === 'APPROVE'
      ? JSON.stringify({ sessionToken: requestId, isSignatureValid: true })
      : JSON.stringify({ sessionToken: requestId });

  const response = await fetch(endpoint, {
    method: 'POST',
    headers,
    body,
  });

  if (!response.ok) {
    const err = await response.json().catch(() => ({}));
    throw new Error(err.detail ?? err.title ?? response.statusText ?? 'MFA decision failed.');
  }

  return response.json();
}
