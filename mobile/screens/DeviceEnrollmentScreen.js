import React, { useState } from 'react';
import { ActivityIndicator, KeyboardAvoidingView, Platform, StyleSheet, Text, TextInput, TouchableOpacity, View } from 'react-native';
import { collectDeviceTelemetry } from '../services/DeviceService';
import { saveAccessToken } from '../services/AuthService';
import { bindSigningKeyToAccount, getDeviceSigningPublicKey, getUserIdFromToken, storeApprovalDeviceId } from '../services/DeviceSigningService';
import { startMfaRealtime } from '../services/RealtimeService';
import { BASE_URL, getApplicationHeaders } from '../config';

export default function DeviceEnrollmentScreen({ navigation }) {
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [recoveryPending, setRecoveryPending] = useState(false);

  const enroll = async () => {
    if (!username.trim() || !password) { setError('Enter your username and password.'); return; }
    setBusy(true);
    setError('');
    try {
      const telemetry = await collectDeviceTelemetry();
      const response = await fetch(`${BASE_URL}/api/devices/bootstrap`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', ...getApplicationHeaders(telemetry.deviceId) },
        body: JSON.stringify({
          username: username.trim(), password,
          deviceId: telemetry.deviceId, deviceName: telemetry.deviceName,
          deviceType: telemetry.deviceType, os: telemetry.os,
          signingPublicKey: await getDeviceSigningPublicKey(username.trim()),
        }),
      });
      const data = await response.json().catch(() => ({}));
      if (!response.ok && response.status === 409 && data.title === 'Device setup unavailable') {
        const signingPublicKey = await getDeviceSigningPublicKey(username.trim());
        const payload = {
          username: username.trim(), password,
          deviceId: telemetry.deviceId, deviceName: telemetry.deviceName,
          deviceType: telemetry.deviceType, os: telemetry.os,
          signingPublicKey,
          posture: telemetry.posture ?? null,
        };
        const pendingResponse = await fetch(`${BASE_URL}/api/devices/register-pending`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', ...getApplicationHeaders(telemetry.deviceId) },
          body: JSON.stringify(payload),
        });
        if (pendingResponse.ok) {
          setRecoveryPending(true);
          setPassword('');
          return;
        }

        const pendingData = await pendingResponse.json().catch(() => ({}));
        if (pendingResponse.status !== 409) {
          throw new Error(pendingData.detail ?? pendingData.title ?? 'Could not register this phone for approval.');
        }

        const recoveryResponse = await fetch(`${BASE_URL}/api/devices/recover-signing-key`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', ...getApplicationHeaders(telemetry.deviceId) },
          body: JSON.stringify(payload),
        });
        const recoveryData = await recoveryResponse.json().catch(() => ({}));
        if (!recoveryResponse.ok) throw new Error(recoveryData.detail ?? recoveryData.title ?? 'Could not submit the device recovery request.');
        setRecoveryPending(true);
        setPassword('');
        return;
      }
      if (!response.ok) throw new Error(data.detail ?? data.title ?? 'Phone setup failed.');
      const accountId = getUserIdFromToken(data.accessToken);
      if (accountId) await bindSigningKeyToAccount(username.trim(), accountId);
      await saveAccessToken(data.accessToken);
      await storeApprovalDeviceId(data.deviceId);
      await startMfaRealtime();
      navigation.replace('Main');
    } catch (exception) {
      setError(exception.message ?? 'Phone setup failed. Check that the backend is running.');
    } finally { setBusy(false); }
  };

  return (
    <KeyboardAvoidingView style={styles.container} behavior={Platform.OS === 'ios' ? 'padding' : 'height'}>
      <View style={styles.card}>
        {recoveryPending ? <Text style={styles.title}>Approval requested</Text> : <Text style={styles.title}>Set up this phone</Text>}
        {recoveryPending ? (
          <>
            <Text style={styles.description}>Your request has been sent to an administrator. Once approved, return to Login and sign in again.</Text>
            <TouchableOpacity style={styles.button} onPress={() => navigation.replace('Login')}>
              <Text style={styles.buttonText}>Back to sign in</Text>
            </TouchableOpacity>
          </>
        ) : <>
        <Text style={styles.description}>Sign in with your Safe Passage account to link this phone for MFA approvals. If you already have another phone linked or are reinstalling the app, an administrator will review your request.</Text>
        <Text style={styles.label}>Email or username</Text>
        <TextInput style={styles.input} autoCapitalize="none" autoCorrect={false} value={username} onChangeText={setUsername} editable={!busy} />
        <Text style={styles.label}>Password</Text>
        <TextInput style={styles.input} secureTextEntry value={password} onChangeText={setPassword} editable={!busy} />
        {error ? <Text accessibilityRole="alert" style={styles.error}>{error}</Text> : null}
        <TouchableOpacity style={[styles.button, busy && styles.disabled]} onPress={enroll} disabled={busy}>
          {busy ? <ActivityIndicator color="#fff" /> : <Text style={styles.buttonText}>Continue</Text>}
        </TouchableOpacity>
        </>}
      </View>
    </KeyboardAvoidingView>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, justifyContent: 'center', backgroundColor: '#020617', padding: 24 },
  card: { backgroundColor: '#0f172a', padding: 22, borderRadius: 12 },
  title: { color: '#f8fafc', fontSize: 22, fontWeight: '700', marginBottom: 10 },
  description: { color: '#94a3b8', lineHeight: 21, marginBottom: 20 },
  label: { color: '#cbd5e1', marginBottom: 7, marginTop: 12 },
  input: { backgroundColor: '#020617', borderWidth: 1, borderColor: '#334155', borderRadius: 7, padding: 13, color: '#f8fafc' },
  error: { color: '#f87171', marginTop: 14 },
  button: { backgroundColor: '#0284c7', padding: 15, borderRadius: 7, alignItems: 'center', marginTop: 22 },
  disabled: { opacity: 0.6 },
  buttonText: { color: '#fff', fontWeight: '700' },
});
