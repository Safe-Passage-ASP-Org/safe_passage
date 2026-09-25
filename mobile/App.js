import React, { useEffect, useRef } from 'react';
import { NavigationContainer, DarkTheme, createNavigationContainerRef } from '@react-navigation/native';
import { createNativeStackNavigator } from '@react-navigation/native-stack';
import { createBottomTabNavigator } from '@react-navigation/bottom-tabs';
import { Ionicons } from '@expo/vector-icons';
import { StatusBar } from 'expo-status-bar';
import * as Notifications from 'expo-notifications';

// Screens
import LoginScreen from './screens/LoginScreen';
import MfaApprovalScreen from './screens/MfaApprovalScreen';
import DashboardScreen from './screens/DashboardScreen';
import DevicesScreen from './screens/DevicesScreen';
import ActivityScreen from './screens/ActivityScreen';
import SettingsScreen from './screens/SettingsScreen';

// Services
import {
  registerForPushNotificationsAsync,
  parseMfaPayload,
} from './services/NotificationService';

const Stack = createNativeStackNavigator();
const Tab = createBottomTabNavigator();

// Navigation ref allows us to navigate from outside React components (e.g. notification handlers)
export const navigationRef = createNavigationContainerRef();

const MainTabs = () => (
  <Tab.Navigator
    screenOptions={({ route }) => ({
      tabBarIcon: ({ focused, color, size }) => {
        const icons = {
          Dashboard: focused ? 'shield-checkmark' : 'shield-checkmark-outline',
          Devices:   focused ? 'hardware-chip'    : 'hardware-chip-outline',
          Activity:  focused ? 'list'             : 'list-outline',
          Settings:  focused ? 'settings'         : 'settings-outline',
        };
        return <Ionicons name={icons[route.name]} size={size} color={color} />;
      },
      tabBarActiveTintColor: '#0ea5e9',
      tabBarInactiveTintColor: '#64748b',
      tabBarStyle: { backgroundColor: '#0f172a', borderTopColor: '#1e293b' },
      headerStyle: { backgroundColor: '#0f172a' },
      headerTintColor: '#fff',
    })}
  >
    <Tab.Screen name="Dashboard" component={DashboardScreen} />
    <Tab.Screen name="Devices"   component={DevicesScreen} />
    <Tab.Screen name="Activity"  component={ActivityScreen} />
    <Tab.Screen name="Settings"  component={SettingsScreen} />
  </Tab.Navigator>
);

const AppTheme = {
  ...DarkTheme,
  colors: {
    ...DarkTheme.colors,
    primary:      '#0ea5e9',
    background:   '#020617',
    card:         '#0f172a',
    text:         '#f8fafc',
    border:       '#1e293b',
    notification: '#ef4444',
  },
};

export default function App() {
  const notificationListener = useRef();
  const responseListener = useRef();

  useEffect(() => {
    // Register device for push notifications on app launch
    registerForPushNotificationsAsync();

    // ── Foreground notification listener ──────────────────────────────────
    // Fires when a notification arrives while the app is OPEN.
    // We parse the MFA payload and immediately open the approval screen.
    notificationListener.current = Notifications.addNotificationReceivedListener(
      (notification) => {
        const isMfa = notification?.request?.content?.data?.type === 'MFA_REQUEST';
        if (isMfa && navigationRef.isReady()) {
          const details = parseMfaPayload(notification);
          navigationRef.navigate('MfaApproval', { details });
        }
      }
    );

    // ── Background / killed notification tap listener ─────────────────────
    // Fires when the user TAPS a notification (app was backgrounded or killed).
    responseListener.current = Notifications.addNotificationResponseReceivedListener(
      (response) => {
        const isMfa = response?.notification?.request?.content?.data?.type === 'MFA_REQUEST';
        if (isMfa && navigationRef.isReady()) {
          const details = parseMfaPayload(response.notification);
          navigationRef.navigate('MfaApproval', { details });
        }
      }
    );

    return () => {
      notificationListener.current?.remove();
      responseListener.current?.remove();
    };
  }, []);

  return (
    <NavigationContainer theme={AppTheme} ref={navigationRef}>
      <StatusBar style="light" />
      <Stack.Navigator screenOptions={{ headerShown: false }}>
        <Stack.Screen name="Login"       component={LoginScreen} />
        <Stack.Screen name="Main"        component={MainTabs} />
        <Stack.Screen
          name="MfaApproval"
          component={MfaApprovalScreen}
          options={{ presentation: 'modal' }}
        />
      </Stack.Navigator>
    </NavigationContainer>
  );
}
