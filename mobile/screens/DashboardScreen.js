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
import { fetchPendingMfaRequests, getMfaStatus, getToken, saveAccessToken } from '../services/AuthService';
import { getApprovalDeviceId, storeApprovalDeviceId } from '../services/DeviceSigningService';
import { deriveSecurityStatus, fetchDashboardData } from '../services/DashboardService';
import { startMfaRealtime } from '../services/RealtimeService';

const INITIAL_SECURITY_STATUS = {
  state: 'checking',
  label: 'Checking...',
  detail: 'Verifying device trust and recent security alerts.',
  icon: 'shield-outline',
  color: '#94a3b8',
};

export default function DashboardScreen({ navigation, route }) {
  const sessionToken = route?.params?.sessionToken ?? null;
  const approvalDeviceId = route?.params?.approvalDeviceId ?? null;

  const [recentActivity, setRecentActivity] = useState([]);
  const [securityStatus, setSecurityStatus] = useState(INITIAL_SECURITY_STATUS);
  const [loading, setLoading] = useState(true);
  const [activityError, setActivityError] = useState('');

  const loadDashboard = useCallback(async (isActive = () => true) => {
    try {
      const deviceId = approvalDeviceId ?? await getApprovalDeviceId();
      if (sessionToken && !(await getToken())) {
        if (isActive()) {
          setRecentActivity([]);
          setActivityError('');
          setSecurityStatus({
            state: 'checking',
            label: 'Approval Pending',
            detail: 'Approve this sign-in on your existing trusted device to finish.',
            icon: 'time-outline',
            color: '#f59e0b',
          });
        }
        return;
      }

      const data = await fetchDashboardData(deviceId);
      if (isActive()) {
        setRecentActivity(data.recentActivity);
        setSecurityStatus(data.securityStatus);
        setActivityError('');
      }

      const pending = await fetchPendingMfaRequests(deviceId);
      if (isActive() && pending.length > 0) {
        const next = pending[0];
        const navState = navigation.getState?.();
        const currentName = navState?.routes?.[navState.index]?.name;
        if (currentName !== 'MfaApproval') {
          navigation.navigate('MfaApproval', {
            details: {
              requestId: next.sessionToken,
              deviceId: next.deviceId,
              ip: next.ipAddress ?? 'Not provided',
              os: next.operatingSystem ?? 'Not provided',
              location: next.location ?? 'Not provided',
              time: next.requestedAt ? new Date(next.requestedAt).toLocaleTimeString() : 'Just now',
            },
          });
        }
      }
    } catch (error) {
      if (isActive()) {
        setRecentActivity([]);
        setSecurityStatus(deriveSecurityStatus({ error }));
        setActivityError(error?.message || 'Could not load live alerts. Check your connection.');
      }
    } finally {
      if (isActive()) setLoading(false);
    }
  }, [approvalDeviceId, navigation, sessionToken]);

  useFocusEffect(useCallback(() => {
    let active = true;
    let refreshing = false;
    setLoading(true);
    setRecentActivity([]);
    setSecurityStatus(INITIAL_SECURITY_STATUS);
    setActivityError('');
    const refresh = async () => {
      if (refreshing) return;
      refreshing = true;
      try {
        await loadDashboard(() => active);
      } finally {
        refreshing = false;
      }
    };

    refresh();
    const intervalId = setInterval(refresh, 30000);
    return () => {
      active = false;
      clearInterval(intervalId);
    };
  }, [loadDashboard]));

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
            const dashboard = await fetchDashboardData(approvedDeviceId);
            if (active) {
              setRecentActivity(dashboard.recentActivity);
              setSecurityStatus(dashboard.securityStatus);
              setActivityError('');
            }
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
        <Ionicons name={securityStatus.icon} size={48} color={securityStatus.color} />
        <Text style={styles.statusTitle}>Security Status</Text>
        <Text style={[styles.statusValue, { color: securityStatus.color }]}>
          {securityStatus.label}
        </Text>
        <Text style={styles.statusDetail}>{securityStatus.detail}</Text>
      </View>

      {/* ── Pending MFA Request ───────────────────────────────── */}
      {sessionToken ? (
        <View style={styles.section} accessibilityRole="summary">
          <Text style={styles.sectionTitle}>Sign-in approval</Text>
          <View style={styles.alertCard}>
            <View style={styles.alertIcon}><Ionicons name="time" size={24} color="#f59e0b" /></View>
            <View style={styles.alertContent}>
              <Text style={styles.alertTitle}>Waiting for approval</Text>
              <Text style={styles.alertDesc}>Approve this request from your existing trusted device.</Text>
            </View>
          </View>
          <View style={{ flexDirection: 'row', gap: 10, marginTop: 10 }}>
            <TouchableOpacity
              style={[styles.retryButton, { flex: 1, alignItems: 'center' }]}
              onPress={() => navigation.navigate('DeviceEnrollment')}
            >
              <Text style={styles.retryButtonText}>Enroll / Recover Phone</Text>
            </TouchableOpacity>
            <TouchableOpacity
              style={[styles.retryButton, { flex: 1, alignItems: 'center', backgroundColor: '#334155' }]}
              onPress={() => navigation.replace('Login')}
            >
              <Text style={styles.retryButtonText}>Back to Login</Text>
            </TouchableOpacity>
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
            <View style={styles.errorContainer}>
              <Text style={styles.errorText}>{activityError}</Text>
              <TouchableOpacity
                style={styles.retryButton}
                onPress={() => {
                  setLoading(true);
                  setActivityError('');
                  loadDashboard();
                }}
              >
                <Text style={styles.retryButtonText}>Retry</Text>
              </TouchableOpacity>
            </View>
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
  statusDetail: { fontSize: 13, color: '#94a3b8', textAlign: 'center', marginTop: 6 },
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
  errorContainer: { padding: 20, alignItems: 'center', gap: 12 },
  errorText: { color: '#fca5a5', textAlign: 'center', fontSize: 14 },
  retryButton: {
    backgroundColor: '#0ea5e9',
    paddingHorizontal: 16,
    paddingVertical: 8,
    borderRadius: 8,
  },
  retryButtonText: { color: '#f8fafc', fontWeight: '600', fontSize: 14 },
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
