import * as Device from 'expo-device';
import { Platform } from 'react-native';
import { getToken } from './AuthService';
import { BASE_URL } from '../config';

/**
 * Registers device with backend.
 * Uses dev token simulator to be 100% compatible with Expo Go on Android.
 */
export async function registerForPushNotificationsAsync() {
  try {
    const token = 'ExponentPushToken[expo-go-dev-simulator]';
    console.log('[Notifications] Device registered with token:', token);

    // Send token to backend
    try {
      const authToken = await getToken();
      if (authToken) {
        await fetch(`${BASE_URL}/api/devices/push-token`, {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            Authorization: `Bearer ${authToken}`,
          },
          body: JSON.stringify({ token, platform: Platform.OS }),
        });
        console.log('[Notifications] Token sent to backend.');
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
    location: data.location ?? 'San Francisco, CA, USA',
    ip: data.ip ?? '192.168.1.42',
    os: data.os ?? 'Windows 11',
    time: data.time ?? new Date().toLocaleTimeString(),
    requestId: data.requestId ?? 'mock-req-001',
  };
}
