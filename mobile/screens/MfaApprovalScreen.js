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
import { submitMfaDecision } from '../services/DeviceService';

export default function MfaApprovalScreen({ route, navigation }) {
  const [approving, setApproving] = useState(false);
  const [denying,   setDenying]   = useState(false);
  const [approved,  setApproved]  = useState(false);

  const details = route.params?.details ?? {};

  // ── Approve: Direct one-tap verification ──────────────────────────────────
  const handleApprove = async () => {
    setApproving(true);
    try {
      await submitMfaDecision(details.requestId, details.deviceId, 'APPROVE');

      // Show success screen for 1.5 seconds then go back
      setApproved(true);
      await new Promise((r) => setTimeout(r, 1500));
      navigation.goBack();
    } catch (err) {
      Alert.alert('Error', 'Could not send approval. Please try again.');
    } finally {
      setApproving(false);
    }
  };

  // ── Deny ─────────────────────────────────────────────────────────────────
  const handleDeny = async () => {
    setDenying(true);
    try {
      await submitMfaDecision(details.requestId, details.deviceId, 'DENY');
    } catch (_) {
      // Best-effort; still dismiss
    } finally {
      setDenying(false);
      navigation.goBack();
    }
  };

  // ── Success screen ────────────────────────────────────────────────────────
  if (approved) {
    return (
      <SafeAreaView style={styles.successContainer}>
        <View style={styles.successIconWrapper}>
          <Ionicons name="shield-checkmark" size={72} color="#10b981" />
        </View>
        <Text style={styles.successTitle}>Sign-in Approved!</Text>
        <Text style={styles.successSubtitle}>Returning to dashboard…</Text>
      </SafeAreaView>
    );
  }

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
          <DetailRow icon="location"  label="Location"          value={details.location ?? 'Not provided'} />
          <DetailRow icon="globe"     label="IP Address"        value={details.ip ?? 'Not provided'} />
          <DetailRow icon="desktop"   label="Operating System"  value={details.os ?? 'Not provided'} />
          <DetailRow icon="time"      label="Time"              value={details.time ?? 'Not provided'} last />
        </View>

        {/* ── Action Buttons ────────────────────────────────────── */}
        <View style={styles.actions}>
          {/* Approve */}
          <TouchableOpacity
            style={[styles.button, styles.approveButton, approving && styles.buttonDisabled]}
            onPress={handleApprove}
            disabled={approving || denying || !details.requestId || !details.deviceId}
          >
            {approving ? (
              <ActivityIndicator color="white" size="small" style={{ marginRight: 8 }} />
            ) : (
              <Ionicons name="checkmark-circle" size={26} color="white" style={styles.btnIcon} />
            )}
            <Text style={styles.buttonText}>{approving ? 'Approving…' : 'Approve'}</Text>
          </TouchableOpacity>

          {/* Deny */}
          <TouchableOpacity
            style={[styles.button, styles.denyButton, denying && styles.buttonDisabled]}
            onPress={handleDeny}
            disabled={approving || denying || !details.requestId || !details.deviceId}
          >
            {denying ? (
              <ActivityIndicator color="white" size="small" style={{ marginRight: 8 }} />
            ) : (
              <Ionicons name="close-circle" size={26} color="white" style={styles.btnIcon} />
            )}
            <Text style={styles.buttonText}>{denying ? 'Denying…' : 'Deny'}</Text>
          </TouchableOpacity>
        </View>

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

  // ── Success state ──────────────────────────────────────────────────────────
  successContainer: {
    flex: 1,
    backgroundColor: '#020617',
    alignItems: 'center',
    justifyContent: 'center',
  },
  successIconWrapper: {
    backgroundColor: '#10b98120',
    padding: 32,
    borderRadius: 60,
    marginBottom: 24,
    borderWidth: 1,
    borderColor: '#10b98140',
  },
  successTitle: {
    fontSize: 28,
    fontWeight: 'bold',
    color: '#f8fafc',
    textAlign: 'center',
  },
  successSubtitle: {
    color: '#94a3b8',
    marginTop: 12,
    textAlign: 'center',
    fontSize: 15,
  },

  // ── Normal state ──────────────────────────────────────────────────────────
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
    marginBottom: 24,
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
});
