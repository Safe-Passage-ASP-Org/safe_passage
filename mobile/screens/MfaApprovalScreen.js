import React, { useState } from 'react';
import {
  View,
  Text,
  StyleSheet,
  TouchableOpacity,
  SafeAreaView,
  ActivityIndicator,
  Alert,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { authenticateWithBiometrics, checkBiometricCapability } from '../services/BiometricService';
import { submitMfaDecision } from '../services/DeviceService';

export default function MfaApprovalScreen({ route, navigation }) {
  const [approving, setApproving] = useState(false);
  const [denying,   setDenying]   = useState(false);
  const [biometricStatus, setBiometricStatus] = useState(null); // 'success' | 'failed' | null

  const details = route.params?.details ?? {
    location:  'San Francisco, CA, USA',
    ip:        '192.168.1.42',
    os:        'Windows 11',
    time:      new Date().toLocaleTimeString(),
    requestId: 'mock-req-001',
  };

  // ── Approve: requires biometric auth ──────────────────────────────────────
  const handleApprove = async () => {
    setApproving(true);
    setBiometricStatus(null);

    // 1. Check capability first to give a helpful error if not enrolled
    const capability = await checkBiometricCapability();
    if (!capability.isAvailable) {
      setApproving(false);
      Alert.alert(
        'Biometrics Unavailable',
        capability.isEnrolled === false
          ? 'No biometrics enrolled on this device. Please set up Face ID or Fingerprint in Settings.'
          : 'This device does not support biometric authentication.',
        [{ text: 'OK' }]
      );
      return;
    }

    // 2. Prompt biometrics
    const authResult = await authenticateWithBiometrics(
      'Confirm your identity to approve this sign-in request.'
    );

    if (!authResult.success) {
      setApproving(false);
      setBiometricStatus('failed');
      Alert.alert('Authentication Failed', authResult.message ?? 'Biometric check failed. Approval not sent.');
      return;
    }

    // 3. Biometrics passed — submit approval to backend
    setBiometricStatus('success');
    try {
      await submitMfaDecision(details.requestId, 'APPROVE');
      navigation.goBack();
    } catch (err) {
      Alert.alert('Error', 'Could not send approval. Please try again.');
    } finally {
      setApproving(false);
    }
  };

  // ── Deny: no biometrics required ─────────────────────────────────────────
  const handleDeny = async () => {
    setDenying(true);
    try {
      await submitMfaDecision(details.requestId, 'DENY');
    } catch (_) {
      // Best-effort; still dismiss
    } finally {
      setDenying(false);
      navigation.goBack();
    }
  };

  return (
    <SafeAreaView style={styles.container}>
      <View style={styles.content}>

        {/* ── Header ────────────────────────────────────────────── */}
        <View style={styles.header}>
          <View style={styles.warningIcon}>
            <Ionicons name="warning" size={56} color="#f59e0b" />
          </View>
          <Text style={styles.title}>Is this you trying{'\n'}to sign in?</Text>
          <Text style={styles.subtitle}>
            A sign-in attempt requires your approval. Review the details carefully.
          </Text>
        </View>

        {/* ── Details Card ──────────────────────────────────────── */}
        <View style={styles.detailsCard}>
          <Text style={styles.detailsTitle}>Sign-in Details</Text>
          <DetailRow icon="location"  label="Location"          value={details.location} />
          <DetailRow icon="globe"     label="IP Address"        value={details.ip} />
          <DetailRow icon="desktop"   label="Operating System"  value={details.os} />
          <DetailRow icon="time"      label="Time"              value={details.time} last />
        </View>

        {/* ── Biometric hint ────────────────────────────────────── */}
        <View style={styles.biometricHint}>
          <Ionicons name="finger-print" size={16} color="#94a3b8" />
          <Text style={styles.biometricHintText}>
            Approving requires biometric verification (Face ID / Fingerprint)
          </Text>
        </View>

        {/* ── Action Buttons ────────────────────────────────────── */}
        <View style={styles.actions}>
          {/* Approve */}
          <TouchableOpacity
            style={[styles.button, styles.approveButton, approving && styles.buttonDisabled]}
            onPress={handleApprove}
            disabled={approving || denying}
          >
            {approving ? (
              <ActivityIndicator color="white" size="small" style={{ marginRight: 8 }} />
            ) : (
              <Ionicons name="checkmark-circle" size={26} color="white" style={styles.btnIcon} />
            )}
            <Text style={styles.buttonText}>{approving ? 'Verifying…' : 'Approve'}</Text>
          </TouchableOpacity>

          {/* Deny */}
          <TouchableOpacity
            style={[styles.button, styles.denyButton, denying && styles.buttonDisabled]}
            onPress={handleDeny}
            disabled={approving || denying}
          >
            {denying ? (
              <ActivityIndicator color="white" size="small" style={{ marginRight: 8 }} />
            ) : (
              <Ionicons name="close-circle" size={26} color="white" style={styles.btnIcon} />
            )}
            <Text style={styles.buttonText}>{denying ? 'Denying…' : 'Deny'}</Text>
          </TouchableOpacity>
        </View>

        {/* ── Biometric result feedback ─────────────────────────── */}
        {biometricStatus === 'failed' && (
          <View style={styles.biometricFeedback}>
            <Ionicons name="close-circle" size={18} color="#ef4444" />
            <Text style={styles.biometricFeedbackText}>Biometric check failed. Approval not sent.</Text>
          </View>
        )}
      </View>
    </SafeAreaView>
  );
}

// ─── Reusable Detail Row ─────────────────────────────────────────────────────
function DetailRow({ icon, label, value, last }) {
  return (
    <View style={[styles.detailRow, last && styles.detailRowLast]}>
      <Ionicons name={icon} size={20} color="#0ea5e9" style={styles.detailIcon} />
      <View style={styles.detailTextGroup}>
        <Text style={styles.detailLabel}>{label}</Text>
        <Text style={styles.detailValue}>{value}</Text>
      </View>
    </View>
  );
}

// ─── Styles ──────────────────────────────────────────────────────────────────
const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: '#020617' },
  content:   { flex: 1, padding: 24, justifyContent: 'center' },

  header:      { alignItems: 'center', marginBottom: 28 },
  warningIcon: {
    backgroundColor: '#f59e0b15',
    padding: 20,
    borderRadius: 50,
    marginBottom: 16,
    borderWidth: 1,
    borderColor: '#f59e0b30',
  },
  title: {
    fontSize: 26,
    fontWeight: 'bold',
    color: '#f8fafc',
    textAlign: 'center',
    marginBottom: 10,
    lineHeight: 34,
  },
  subtitle: { fontSize: 15, color: '#94a3b8', textAlign: 'center', lineHeight: 22 },

  detailsCard: {
    backgroundColor: '#0f172a',
    borderRadius: 16,
    padding: 20,
    marginBottom: 16,
    borderWidth: 1,
    borderColor: '#1e293b',
  },
  detailsTitle: { fontSize: 14, fontWeight: '600', color: '#64748b', marginBottom: 16, textTransform: 'uppercase', letterSpacing: 1 },
  detailRow: {
    flexDirection: 'row',
    alignItems: 'center',
    paddingVertical: 12,
    borderBottomWidth: 1,
    borderBottomColor: '#1e293b',
  },
  detailRowLast:  { borderBottomWidth: 0 },
  detailIcon:     { marginRight: 14 },
  detailTextGroup: { flex: 1 },
  detailLabel:    { fontSize: 12, color: '#64748b', marginBottom: 2 },
  detailValue:    { fontSize: 16, color: '#e2e8f0', fontWeight: '500' },

  biometricHint: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    marginBottom: 20,
    gap: 6,
  },
  biometricHintText: { color: '#64748b', fontSize: 13 },

  actions:  { gap: 14 },
  button: {
    flexDirection: 'row',
    padding: 18,
    borderRadius: 14,
    alignItems: 'center',
    justifyContent: 'center',
  },
  approveButton:  { backgroundColor: '#10b981' },
  denyButton:     { backgroundColor: '#ef4444' },
  buttonDisabled: { opacity: 0.6 },
  btnIcon:        { marginRight: 10 },
  buttonText:     { color: '#ffffff', fontSize: 20, fontWeight: 'bold' },

  biometricFeedback: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    marginTop: 14,
    gap: 6,
  },
  biometricFeedbackText: { color: '#ef4444', fontSize: 14 },
});
