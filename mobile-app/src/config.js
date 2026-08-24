import { Platform } from 'react-native';

// Replace with your computer's Wi-Fi IP address when testing on a physical phone
const LOCAL_WIFI_IP = '192.168.1.50'; 

export const API_BASE_URL = Platform.select({
  android: 'http://10.0.2.2:5000/api',        // For Android Studio Emulator
  ios: 'http://localhost:5000/api',            // For iOS Simulator
  default: `http://${LOCAL_WIFI_IP}:5000/api` // For Physical Phone via Expo Go
});