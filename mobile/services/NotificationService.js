import * as Device from 'expo-device';
import * as Notifications from 'expo-notifications';
import Constants from 'expo-constants';
import { Platform } from 'react-native';
import { getToken } from './AuthService';
import { getApprovalDeviceId } from './DeviceSigningService';
import { BASE_URL } from '../config';

const SIMULATOR_FALLBACK_TOKEN = 'ExponentPushToken[expo-go-dev-simulator]';

/**
 * Registers the device for push notifications and sends the token to the backend.
 * Attempts real Expo push token acquisition on physical native devices, falling back
 * to the simulator token on web or unconfigured local dev environments.
 */
export async function registerForPushNotificationsAsync() {
  try {
    let token = SIMULATOR_FALLBACK_TOKEN;

    if (Platform.OS !== 'web' && Device.isDevice) {
      try {
        const existing = await Notifications.getPermissionsAsync();
        let finalStatus = existing.status;
        if (finalStatus !== 'granted') {
          const requested = await Notifications.requestPermissionsAsync();
          finalStatus = requested.status;
        }
        if (finalStatus === 'granted') {
          const projectId = Constants?.expoConfig?.extra?.eas?.projectId
            ?? Constants?.easConfig?.projectId;
          const response = projectId
            ? await Notifications.getExpoPushTokenAsync({ projectId })
            : await Notifications.getExpoPushTokenAsync();
          if (response?.data) {
            token = response.data;
          }
        }
      } catch (nativeErr) {
        console.warn('[Notifications] Using fallback token:', nativeErr.message);
      }
    }

    try {
      const authToken = await getToken();
      if (authToken) {
        const deviceId = await getApprovalDeviceId();
        await fetch(`${BASE_URL}/api/devices/push-token`, {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            Authorization: `Bearer ${authToken}`,
          },
          body: JSON.stringify({ token, platform: Platform.OS, deviceId }),
        });
      }
    } catch (e) {
      console.warn('[Notifications] Failed to send token to backend:', e.message);
    }

    return token;
  } catch (error) {
    console.warn('[Notifications] Error in registration:', error.message);
    return null;
  }
}

/**
 * Parses an incoming notification payload and extracts MFA sign-in details.
 */
export function parseMfaPayload(notification) {
  const data = notification?.request?.content?.data ?? notification ?? {};
  return {
    location: data.location ?? 'Not provided',
    ip: data.ip ?? 'Not provided',
    os: data.os ?? 'Not provided',
    time: data.time ?? new Date().toLocaleTimeString(),
    requestId: data.requestId ?? null,
  };
}
