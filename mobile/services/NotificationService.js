import * as Notifications from 'expo-notifications';
import * as Device from 'expo-device';
import { Platform } from 'react-native';

// Configure how incoming notifications are displayed when the app is in foreground
Notifications.setNotificationHandler({
  handleNotification: async () => ({
    shouldShowBanner: true,
    shouldShowList: true,
    shouldPlaySound: true,
    shouldSetBadge: true,
  }),
});

/**
 * Requests notification permissions and registers for push notifications.
 * Returns the Expo push token string, or null if permissions were denied.
 */
export async function registerForPushNotificationsAsync() {
  if (!Device.isDevice) {
    console.warn('[Notifications] Must use a physical device for push notifications.');
    return null;
  }

  const { status: existingStatus } = await Notifications.getPermissionsAsync();
  let finalStatus = existingStatus;

  if (existingStatus !== 'granted') {
    const { status } = await Notifications.requestPermissionsAsync();
    finalStatus = status;
  }

  if (finalStatus !== 'granted') {
    console.warn('[Notifications] Permission denied for push notifications.');
    return null;
  }

  // For production, replace with your own projectId from app.json or EAS
  const token = (await Notifications.getExpoPushTokenAsync()).data;
  console.log('[Notifications] Expo Push Token:', token);

  // Android requires a notification channel
  if (Platform.OS === 'android') {
    await Notifications.setNotificationChannelAsync('mfa-requests', {
      name: 'MFA Requests',
      importance: Notifications.AndroidImportance.MAX,
      vibrationPattern: [0, 250, 250, 250],
      lightColor: '#0ea5e9',
      sound: 'default',
    });
  }

  return token;
}

/**
 * Parses an incoming notification payload and extracts MFA sign-in details.
 * In production this payload would come from your backend push message.
 */
export function parseMfaPayload(notification) {
  const data = notification?.request?.content?.data ?? {};
  return {
    location: data.location ?? 'Unknown Location',
    ip: data.ip ?? 'Unknown IP',
    os: data.os ?? 'Unknown OS',
    time: data.time ?? new Date().toLocaleTimeString(),
    requestId: data.requestId ?? null,
  };
}
