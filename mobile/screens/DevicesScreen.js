import React, { useState, useEffect } from 'react';
import {
  View,
  Text,
  StyleSheet,
  ScrollView,
  TouchableOpacity,
  ActivityIndicator,
  Alert,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { collectDeviceTelemetry, registerDeviceWithBackend } from '../services/DeviceService';
import { getToken } from '../services/AuthService';
import { BASE_URL } from '../config';

const STATUS_COLORS = {
  Trusted:   '#10b981',
  Untrusted: '#ef4444',
  Pending:   '#f59e0b',
};

// ─── Sub-components ──────────────────────────────────────────────────────────

function DeviceCard({ item }) {
  const color = STATUS_COLORS[item.status] ?? '#64748b';
  return (
    <View style={styles.deviceCard}>
      <View style={styles.deviceIcon}>
        <Ionicons name={item.type ?? 'hardware-chip-outline'} size={28} color="#0ea5e9" />
      </View>
      <View style={styles.deviceInfo}>
        <Text style={styles.deviceName}>{item.name}</Text>
        <Text style={styles.deviceDate}>{item.date}</Text>
      </View>
      <View style={[styles.statusBadge, { backgroundColor: `${color}20` }]}>
        <Text style={[styles.statusText, { color }]}>{item.status}</Text>
      </View>
    </View>
  );
}

// ─── Registration Result Overlay ─────────────────────────────────────────────
function RegistrationResult({ result, telemetry, onDismiss }) {
  return (
    <View style={styles.resultOverlay}>
      <View style={styles.resultCard}>
        <View style={styles.resultIconWrapper}>
          <Ionicons name="time" size={56} color="#f59e0b" />
        </View>
        <Text style={styles.resultTitle}>Device Submitted</Text>
        <View style={styles.statusChip}>
          <Text style={styles.statusChipText}>Status: {result.status}</Text>
        </View>
        <Text style={styles.resultMessage}>{result.message}</Text>

        <View style={styles.telemetryBox}>
          <Text style={styles.telemetryTitle}>Collected Telemetry</Text>
          <TelemetryRow label="Device ID"      value={telemetry.deviceId} />
          <TelemetryRow label="Device Name"    value={telemetry.deviceName} />
          <TelemetryRow label="OS"             value={`${telemetry.osName} ${telemetry.osVersion}`} />
          <TelemetryRow label="Model"          value={telemetry.modelName} />
          <TelemetryRow label="HW Fingerprint" value={telemetry.hardwareFingerprint} mono />
        </View>

        <TouchableOpacity style={styles.dismissButton} onPress={onDismiss}>
          <Text style={styles.dismissButtonText}>Done</Text>
        </TouchableOpacity>
      </View>
    </View>
  );
}

function TelemetryRow({ label, value, mono }) {
  return (
    <View style={styles.telemetryRow}>
      <Text style={styles.telemetryLabel}>{label}</Text>
      <Text style={[styles.telemetryValue, mono && styles.mono]} numberOfLines={1} ellipsizeMode="middle">
        {value}
      </Text>
    </View>
  );
}

// ─── Main Screen ─────────────────────────────────────────────────────────────
export default function DevicesScreen() {
  const [devices, setDevices]                   = useState([]);
  const [fetching, setFetching]                 = useState(true);
  const [fetchError, setFetchError]             = useState('');
  const [loading, setLoading]                   = useState(false);
  const [registrationResult, setRegistrationResult] = useState(null);
  const [collectedTelemetry, setCollectedTelemetry] = useState(null);

  // ── Fetch devices from backend on mount ──────────────────────────────────
  useEffect(() => {
    (async () => {
      try {
        const token = await getToken();
        const response = await fetch(`${BASE_URL}/api/devices`, {
          headers: token ? { Authorization: `Bearer ${token}` } : {},
        });
        if (!response.ok) throw new Error(response.statusText);
        const data = await response.json();
        setDevices(data);
      } catch (err) {
        setFetchError('Failed to load devices. Check your connection.');
      } finally {
        setFetching(false);
      }
    })();
  }, []);

  const handleRegisterDevice = async () => {
    setLoading(true);
    try {
      // Step 1: Collect telemetry
      const telemetry = await collectDeviceTelemetry();
      setCollectedTelemetry(telemetry);

      // Step 2: Submit to backend
      const result = await registerDeviceWithBackend(telemetry);
      setRegistrationResult(result);
    } catch (err) {
      Alert.alert('Registration Failed', err.message ?? 'An unexpected error occurred.');
    } finally {
      setLoading(false);
    }
  };

  return (
    <View style={styles.container}>
      {/* ── Device List ─────────────────────────────────────── */}
      <ScrollView contentContainerStyle={styles.listContainer}>
        {fetching && (
          <ActivityIndicator color="#0ea5e9" size="large" style={{ marginTop: 40 }} />
        )}
        {fetchError ? (
          <Text style={styles.errorText}>{fetchError}</Text>
        ) : null}
        {!fetching && !fetchError && devices.length === 0 && (
          <Text style={styles.emptyText}>No devices registered yet.</Text>
        )}
        {devices.map((item) => (
          <DeviceCard key={item.id} item={item} />
        ))}
      </ScrollView>

      {/* ── Register Button ──────────────────────────────────── */}
      <TouchableOpacity
        style={[styles.registerButton, loading && styles.registerButtonDisabled]}
        onPress={handleRegisterDevice}
        disabled={loading}
      >
        {loading ? (
          <>
            <ActivityIndicator color="white" style={{ marginRight: 10 }} />
            <Text style={styles.registerButtonText}>Collecting Telemetry…</Text>
          </>
        ) : (
          <>
            <Ionicons name="add" size={24} color="white" />
            <Text style={styles.registerButtonText}>Register this Device</Text>
          </>
        )}
      </TouchableOpacity>

      {/* ── Registration Result Overlay ──────────────────────── */}
      {registrationResult && collectedTelemetry && (
        <RegistrationResult
          result={registrationResult}
          telemetry={collectedTelemetry}
          onDismiss={() => {
            setRegistrationResult(null);
            setCollectedTelemetry(null);
          }}
        />
      )}
    </View>
  );
}

// ─── Styles ──────────────────────────────────────────────────────────────────
const styles = StyleSheet.create({
  container:    { flex: 1, backgroundColor: '#020617' },
  listContainer: { padding: 16 },

  errorText:  { color: '#ef4444', textAlign: 'center', marginTop: 40, fontSize: 15 },
  emptyText:  { color: '#64748b', textAlign: 'center', marginTop: 40, fontSize: 15 },

  deviceCard: {
    flexDirection: 'row',
    alignItems: 'center',
    backgroundColor: '#0f172a',
    borderRadius: 12,
    padding: 16,
    marginBottom: 12,
    borderWidth: 1,
    borderColor: '#1e293b',
  },
  deviceIcon: {
    backgroundColor: '#020617',
    padding: 12,
    borderRadius: 12,
    marginRight: 16,
  },
  deviceInfo:  { flex: 1 },
  deviceName:  { fontSize: 16, fontWeight: '600', color: '#f8fafc', marginBottom: 4 },
  deviceDate:  { fontSize: 12, color: '#64748b' },
  statusBadge: { paddingHorizontal: 12, paddingVertical: 6, borderRadius: 16 },
  statusText:  { fontSize: 12, fontWeight: '600' },

  registerButton: {
    flexDirection: 'row',
    backgroundColor: '#0ea5e9',
    margin: 16,
    padding: 16,
    borderRadius: 12,
    alignItems: 'center',
    justifyContent: 'center',
  },
  registerButtonDisabled: { backgroundColor: '#0ea5e980' },
  registerButtonText: { color: 'white', fontSize: 16, fontWeight: 'bold', marginLeft: 8 },

  // Overlay
  resultOverlay: {
    ...StyleSheet.absoluteFillObject,
    backgroundColor: '#00000090',
    justifyContent: 'center',
    alignItems: 'center',
    padding: 24,
  },
  resultCard: {
    backgroundColor: '#0f172a',
    borderRadius: 20,
    padding: 24,
    width: '100%',
    borderWidth: 1,
    borderColor: '#1e293b',
    alignItems: 'center',
  },
  resultIconWrapper: {
    backgroundColor: '#f59e0b20',
    padding: 20,
    borderRadius: 50,
    marginBottom: 16,
  },
  resultTitle: {
    fontSize: 22,
    fontWeight: 'bold',
    color: '#f8fafc',
    marginBottom: 12,
  },
  statusChip: {
    backgroundColor: '#f59e0b20',
    borderWidth: 1,
    borderColor: '#f59e0b60',
    borderRadius: 20,
    paddingHorizontal: 16,
    paddingVertical: 6,
    marginBottom: 16,
  },
  statusChipText: { color: '#f59e0b', fontWeight: '700', fontSize: 14 },
  resultMessage:  { color: '#94a3b8', textAlign: 'center', marginBottom: 20, lineHeight: 22 },

  telemetryBox: {
    width: '100%',
    backgroundColor: '#020617',
    borderRadius: 12,
    padding: 16,
    marginBottom: 20,
    borderWidth: 1,
    borderColor: '#1e293b',
  },
  telemetryTitle: { fontSize: 13, fontWeight: '600', color: '#64748b', marginBottom: 12, textTransform: 'uppercase', letterSpacing: 1 },
  telemetryRow:   { flexDirection: 'row', justifyContent: 'space-between', marginBottom: 8 },
  telemetryLabel: { fontSize: 13, color: '#64748b', flex: 1 },
  telemetryValue: { fontSize: 13, color: '#e2e8f0', flex: 2, textAlign: 'right' },
  mono:           { fontFamily: 'monospace', color: '#0ea5e9' },

  dismissButton: {
    backgroundColor: '#0ea5e9',
    borderRadius: 10,
    paddingVertical: 14,
    paddingHorizontal: 40,
  },
  dismissButtonText: { color: '#fff', fontSize: 16, fontWeight: 'bold' },
});
