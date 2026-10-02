import React, { useState, useEffect } from 'react';
import { View, Text, StyleSheet, Switch, TouchableOpacity, ScrollView, Alert, ActivityIndicator } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { logout, getToken } from '../services/AuthService';

/**
 * Decode the payload from a JWT token without verifying the signature.
 * Only used to display friendly UI info — never trusted for security decisions.
 */
function decodeJwtPayload(token) {
  try {
    const base64 = token.split('.')[1];
    const padded = base64.replace(/-/g, '+').replace(/_/g, '/').padEnd(base64.length + (4 - (base64.length % 4)) % 4, '=');
    const json = decodeURIComponent(
      atob(padded)
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

  useEffect(() => {
    (async () => {
      try {
        const token = await getToken();
        if (token) {
          const payload = decodeJwtPayload(token);
          if (payload) {
            setUserInfo({
              name: payload.name ?? payload.unique_name ?? payload.username ?? 'John Doe',
              email: payload.email ?? 'john.doe@enterprise.com',
              role: payload.role ?? 'User',
              initials: ((payload.name ?? payload.unique_name ?? 'JD')
                .split(' ')
                .map((n) => n[0])
                .join('')
                .toUpperCase()
                .substring(0, 2)),
            });
          }
        }
      } catch (_) {
        // Fall back to defaults
      } finally {
        setLoadingUser(false);
      }
    })();
  }, []);

  const handleLogout = async () => {
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

  const displayName = userInfo?.name ?? 'John Doe';
  const displayEmail = userInfo?.email ?? 'john.doe@enterprise.com';
  const displayInitials = userInfo?.initials ?? 'JD';
  const displayRole = userInfo?.role ?? 'User';

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
            <Text style={styles.profileEmail}>{displayEmail}</Text>
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
