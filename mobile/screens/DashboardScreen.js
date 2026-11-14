import React, { useState, useEffect, useCallback } from 'react';
import {
  View,
  Text,
  StyleSheet,
  ScrollView,
  TouchableOpacity,
  ActivityIndicator,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { useFocusEffect } from '@react-navigation/native';
import { getMfaStatus, getToken, saveAccessToken } from '../services/AuthService';
import { getApprovalDeviceId, storeApprovalDeviceId } from '../services/DeviceSigningService';
import { BASE_URL, getApplicationHeaders } from '../config';
import { startMfaRealtime } from '../services/RealtimeService';

async function fetchRecentActivity(deviceId) {
  const token = await getToken();
  if (!token || !deviceId) throw new Error('Sign in on an enrolled device.');
  const response = await fetch(`${BASE_URL}/api/activity`, {
    headers: { Authorization: `Bearer ${token}`, ...getApplicationHeaders(deviceId) },
  });
  if (!response.ok) throw new Error('Could not load activity.');
  return response.json();
}

export default function DashboardScreen({ navigation, route }) {
  const sessionToken = route?.params?.sessionToken ?? null;
  const approvalDeviceId = route?.params?.approvalDeviceId ?? null;

  const [recentActivity, setRecentActivity] = useState([]);
  const [loading, setLoading] = useState(true);

  const [activityError, setActivityError] = useState('');

  useFocusEffect(useCallback(() => {
    let active = true;
    let refreshing = false;
    setLoading(true);
    setRecentActivity([]);
    setActivityError('');
    const loadRecentActivity = async () => {
      if (refreshing) return;
      refreshing = true;
      try {
        const deviceId = approvalDeviceId ?? await getApprovalDeviceId();
        const data = await fetchRecentActivity(deviceId);
        if (active) {
          setRecentActivity(data.slice(0, 3));
          setActivityError('');
        }
      } catch (error) {
        if (active) setActivityError('Could not load live alerts. Check your connection.');
      } finally {
        refreshing = false;
        if (active) setLoading(false);
      }
    };

    loadRecentActivity();
    const intervalId = setInterval(loadRecentActivity, 30000);
    return () => {
      active = false;
      clearInterval(intervalId);
    };
  }, [approvalDeviceId]));

  useEffect(() => {
    if (!sessionToken) return undefined;

    const controller = new AbortController();
    let active = true;

    (async () => {
      try {
        while (active) {
          const result = await getMfaStatus(sessionToken, controller.signal, approvalDeviceId);
          const status = String(result.status ?? '').toLowerCase();

          if (status === 'approved') {
            await saveAccessToken(result.accessToken);
            const approvedDeviceId = result.deviceId ?? approvalDeviceId;
            await storeApprovalDeviceId(approvedDeviceId);
            await startMfaRealtime();
            if (!active) return;
            navigation.setParams({ sessionToken: null, approvalDeviceId: approvedDeviceId });
            const activity = await fetchRecentActivity(approvedDeviceId);
            if (active) setRecentActivity(activity.slice(0, 3));
            return;
          }

          if (status === 'denied' || status === 'expired') {
            navigation.setParams({ sessionToken: null });
            return;
          }

          await new Promise((resolve) => setTimeout(resolve, 1000));
        }
      } catch (error) {
        if (active && error.name !== 'AbortError') {
          console.warn('[Auth] MFA status polling failed:', error.message);
        }
      }
    })();

    return () => {
      active = false;
      controller.abort();
    };
  }, [approvalDeviceId, navigation, sessionToken]);

  return (
    <ScrollView style={styles.container}>
      {/* ── Security Status Card ─────────────────────────────── */}
      <View style={styles.statusCard}>
        <Ionicons name="shield-checkmark" size={48} color="#10b981" />
        <Text style={styles.statusTitle}>Security Status</Text>
        <Text style={styles.statusValue}>Protected</Text>
      </View>

      {/* ── Pending MFA Request ───────────────────────────────── */}
      {sessionToken ? (
        <View style={styles.section} accessibilityRole="summary">
          <Text style={styles.sectionTitle}>Sign-in approval</Text>
          <View style={styles.alertCard}>
            <View style={styles.alertIcon}><Ionicons name="time" size={24} color="#f59e0b" /></View>
            <View style={styles.alertContent}>
              <Text style={styles.alertTitle}>Waiting for approval</Text>
              <Text style={styles.alertDesc}>Approve this request from a trusted device.</Text>
            </View>
          </View>
        </View>
      ) : null}

      {/* ── Recent Activity ──────────────────────────────────── */}
      <View style={styles.section}>
        <Text style={styles.sectionTitle}>Recent Alerts</Text>
        <View style={styles.card}>
          {loading && (
            <ActivityIndicator color="#0ea5e9" style={{ padding: 20 }} />
          )}
          {!loading && activityError ? (
            <Text style={styles.emptyText}>{activityError}</Text>
          ) : null}
          {!loading && !activityError && recentActivity.length === 0 && (
            <Text style={styles.emptyText}>No recent alerts.</Text>
          )}
          {!loading && recentActivity.map((item, idx) => (
            <View
              key={item.id}
              style={[styles.alertRow, idx === recentActivity.length - 1 && styles.lastAlertRow]}
            >
              <Ionicons name={item.icon ?? 'shield-checkmark'} size={24} color={item.color ?? '#10b981'} />
              <View style={styles.alertContent}>
                <Text style={styles.alertTitle}>{item.action}</Text>
                <Text style={styles.alertDesc}>{item.target} • {item.time}</Text>
              </View>
            </View>
          ))}
        </View>
      </View>
    </ScrollView>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    backgroundColor: '#020617',
    padding: 16,
  },
  statusCard: {
    backgroundColor: '#0f172a',
    borderRadius: 16,
    padding: 24,
    alignItems: 'center',
    marginBottom: 24,
    borderWidth: 1,
    borderColor: '#1e293b',
  },
  statusTitle: { fontSize: 16, color: '#94a3b8', marginTop: 12 },
  statusValue: { fontSize: 28, fontWeight: 'bold', color: '#10b981', marginTop: 4 },
  section: { marginBottom: 24 },
  sectionTitle: { fontSize: 18, fontWeight: '600', color: '#f8fafc', marginBottom: 12 },
  alertCard: {
    flexDirection: 'row',
    alignItems: 'center',
    backgroundColor: '#0f172a',
    borderRadius: 12,
    padding: 16,
    borderWidth: 1,
    borderColor: '#f59e0b',
  },
  alertIcon: {
    backgroundColor: '#f59e0b20',
    padding: 8,
    borderRadius: 8,
    marginRight: 12,
  },
  alertContent: { flex: 1 },
  emptyText: { color: '#64748b', textAlign: 'center', padding: 20 },
  alertTitle: { fontSize: 16, fontWeight: '500', color: '#f8fafc' },
  alertDesc: { fontSize: 14, color: '#94a3b8', marginTop: 2 },
  card: {
    backgroundColor: '#0f172a',
    borderRadius: 12,
    borderWidth: 1,
    borderColor: '#1e293b',
  },
  alertRow: {
    flexDirection: 'row',
    alignItems: 'center',
    padding: 16,
    borderBottomWidth: 1,
    borderBottomColor: '#1e293b',
    gap: 12,
  },
  lastAlertRow: { borderBottomWidth: 0 },
});
