import React, { useState } from 'react';
import { View, Text, StyleSheet, Switch, TouchableOpacity, ScrollView, Alert, ActivityIndicator } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { useFocusEffect } from '@react-navigation/native';
import * as Clipboard from 'expo-clipboard';
import { logout, getToken } from '../services/AuthService';
import { getApprovalDeviceId, getDeviceSigningPublicKey } from '../services/DeviceSigningService';
import { stopMfaRealtime } from '../services/RealtimeService';

/**
 * Decode the payload from a JWT token without verifying the signature.
 * Only used to display friendly UI info — never trusted for security decisions.
 *
 * Uses a pure-JS base64 decoder so it works on the Hermes JS engine (no atob).
 */
function base64UrlDecode(str) {
  // Convert base64url → base64, then pad
  const base64 = str.replace(/-/g, '+').replace(/_/g, '/');
  const padded = base64.padEnd(base64.length + (4 - (base64.length % 4)) % 4, '=');
  // Pure-JS base64 decode — works on Hermes without atob
  const chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=';
  let bytes = '';
  let i = 0;
  while (i < padded.length) {
    const e1 = chars.indexOf(padded[i++]);
    const e2 = chars.indexOf(padded[i++]);
    const e3 = chars.indexOf(padded[i++]);
    const e4 = chars.indexOf(padded[i++]);
    bytes += String.fromCharCode(((e1 << 2) | (e2 >> 4)) & 0xff);
    if (e3 !== 64) bytes += String.fromCharCode(((e2 << 4) | (e3 >> 2)) & 0xff);
    if (e4 !== 64) bytes += String.fromCharCode(((e3 << 6) | e4) & 0xff);
  }
  return bytes;
}

function decodeJwtPayload(token) {
  try {
    const rawPayload = token.split('.')[1];
    if (!rawPayload) return null;
    const decoded = base64UrlDecode(rawPayload);
    const json = decodeURIComponent(
      decoded
        .split('')
        .map((c) => '%' + ('00' + c.charCodeAt(0).toString(16)).slice(-2))
        .join('')
    );
    return JSON.parse(json);
  } catch {
    return null;
  }
}

export default function SettingsScreen({ navigation }) {
  const [notificationsEnabled, setNotificationsEnabled] = useState(true);
  const [userInfo, setUserInfo] = useState(null);
  const [loadingUser, setLoadingUser] = useState(true);

  useFocusEffect(React.useCallback(() => {
    let active = true;
    setUserInfo(null);
    setLoadingUser(true);
    (async () => {
      try {
        const token = await getToken();
        const payload = token ? decodeJwtPayload(token) : null;
        const name = payload?.name
          ?? payload?.unique_name
          ?? payload?.username
          ?? payload?.['http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name']
          ?? '';
        const email = payload?.email
          ?? payload?.['http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress']
          ?? '';
        const role = payload?.role
          ?? payload?.['http://schemas.microsoft.com/ws/2008/06/identity/claims/role']
          ?? '';
        const nameParts = name.trim().split(/\s+/).filter(Boolean);
        const initials = nameParts.length > 1
          ? nameParts.slice(0, 2).map((part) => part[0]).join('').toUpperCase()
          : (nameParts[0] ?? '?').slice(0, 2).toUpperCase();
        if (active) setUserInfo({ name, email, role, initials });
      } finally {
        if (active) setLoadingUser(false);
      }
    })();
    return () => { active = false; };
  }, []));

  const handleLogout = async () => {
    await stopMfaRealtime();
    await logout();
    navigation.replace('Login');
  };

  const handleChangePin = () => {
    Alert.alert(
      'Change PIN',
      'PIN management will be available in a future update.',
      [{ text: 'OK' }]
    );
  };

  const handleCopyEnrollmentInfo = async () => {
    try {
      const deviceId = await getApprovalDeviceId();
      if (!deviceId) throw new Error('Sign in first to identify the trusted MFA device.');
      const signingPublicKey = await getDeviceSigningPublicKey();
      await Clipboard.setStringAsync(JSON.stringify({ deviceId, signingPublicKey }));
      Alert.alert('Enrollment info copied', 'Use this public key to enroll the trusted device. The private key stays on this phone.');
    } catch (error) {
      Alert.alert('Could not copy enrollment info', error.message);
    }
  };

  const displayName = userInfo?.name || 'Signed-in account';
  const displayEmail = userInfo?.email;
  const displayInitials = userInfo?.initials || '?';
  const displayRole = userInfo?.role || 'Role unavailable';

  return (
    <ScrollView style={styles.container}>
      {/* ── Profile Section ─────────────────────────────────── */}
      <View style={styles.profileSection}>
        {loadingUser ? (
          <ActivityIndicator color="#0ea5e9" style={{ marginBottom: 20 }} />
        ) : (
          <>
            <View style={styles.avatar}>
              <Text style={styles.avatarText}>{displayInitials}</Text>
            </View>
            <Text style={styles.profileName}>{displayName}</Text>
            {displayEmail ? <Text style={styles.profileEmail}>{displayEmail}</Text> : null}
            <View style={styles.roleBadge}>
              <Text style={styles.roleText}>{displayRole}</Text>
            </View>
          </>
        )}
      </View>

      {/* ── Security Section ─────────────────────────────────── */}
      <View style={styles.section}>
        <Text style={styles.sectionTitle}>Security</Text>
        <View style={styles.card}>
          <TouchableOpacity style={[styles.settingRow, styles.lastSettingRow]} onPress={handleChangePin}>
            <View style={styles.settingInfo}>
              <Ionicons name="keypad" size={24} color="#0ea5e9" style={styles.settingIcon} />
              <Text style={styles.settingText}>Change PIN</Text>
            </View>
            <Ionicons name="chevron-forward" size={20} color="#64748b" />
          </TouchableOpacity>
          <TouchableOpacity style={[styles.settingRow, styles.lastSettingRow]} onPress={handleCopyEnrollmentInfo}>
            <View style={styles.settingInfo}>
              <Ionicons name="key" size={24} color="#0ea5e9" style={styles.settingIcon} />
              <Text style={styles.settingText}>Copy device enrollment info</Text>
            </View>
            <Ionicons name="copy-outline" size={20} color="#64748b" />
          </TouchableOpacity>
        </View>
      </View>

      {/* ── Preferences Section ──────────────────────────────── */}
      <View style={styles.section}>
        <Text style={styles.sectionTitle}>Preferences</Text>
        <View style={styles.card}>
          <View style={[styles.settingRow, styles.lastSettingRow]}>
            <View style={styles.settingInfo}>
              <Ionicons name="notifications" size={24} color="#0ea5e9" style={styles.settingIcon} />
              <Text style={styles.settingText}>Push Notifications</Text>
            </View>
            <Switch
              value={notificationsEnabled}
              onValueChange={setNotificationsEnabled}
              trackColor={{ false: '#1e293b', true: '#0ea5e9' }}
              thumbColor={notificationsEnabled ? '#ffffff' : '#94a3b8'}
            />
          </View>
        </View>
      </View>

      {/* ── Sign Out ─────────────────────────────────────────── */}
      <TouchableOpacity style={styles.logoutButton} onPress={handleLogout}>
        <Ionicons name="log-out" size={24} color="#ef4444" />
        <Text style={styles.logoutText}>Sign Out</Text>
      </TouchableOpacity>
    </ScrollView>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: '#020617' },
  profileSection: {
    alignItems: 'center',
    padding: 32,
    borderBottomWidth: 1,
    borderBottomColor: '#1e293b',
  },
  avatar: {
    width: 80,
    height: 80,
    borderRadius: 40,
    backgroundColor: '#0ea5e9',
    alignItems: 'center',
    justifyContent: 'center',
    marginBottom: 16,
  },
  avatarText: { fontSize: 32, fontWeight: 'bold', color: '#ffffff' },
  profileName: { fontSize: 24, fontWeight: 'bold', color: '#f8fafc', marginBottom: 4 },
  profileEmail: { fontSize: 14, color: '#94a3b8', marginBottom: 8 },
  roleBadge: {
    backgroundColor: '#0ea5e920',
    borderWidth: 1,
    borderColor: '#0ea5e940',
    borderRadius: 12,
    paddingHorizontal: 12,
    paddingVertical: 4,
  },
  roleText: { color: '#0ea5e9', fontSize: 13, fontWeight: '600' },
  section: { padding: 16 },
  sectionTitle: {
    fontSize: 14,
    fontWeight: '600',
    color: '#64748b',
    marginBottom: 12,
    textTransform: 'uppercase',
    letterSpacing: 1,
  },
  card: {
    backgroundColor: '#0f172a',
    borderRadius: 12,
    borderWidth: 1,
    borderColor: '#1e293b',
    overflow: 'hidden',
  },
  settingRow: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    padding: 16,
    borderBottomWidth: 1,
    borderBottomColor: '#1e293b',
  },
  lastSettingRow: { borderBottomWidth: 0 },
  settingInfo: { flexDirection: 'row', alignItems: 'center' },
  settingIcon: { marginRight: 12 },
  settingText: { fontSize: 16, color: '#f8fafc' },
  logoutButton: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    padding: 16,
    margin: 16,
    backgroundColor: '#ef444420',
    borderRadius: 12,
    borderWidth: 1,
    borderColor: '#ef444440',
  },
  logoutText: { fontSize: 16, fontWeight: 'bold', color: '#ef4444', marginLeft: 8 },
});
