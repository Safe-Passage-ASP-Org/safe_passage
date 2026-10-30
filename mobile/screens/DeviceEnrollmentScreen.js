import React, { useState } from 'react';
import { ActivityIndicator, KeyboardAvoidingView, Platform, StyleSheet, Text, TextInput, TouchableOpacity, View } from 'react-native';
import { collectDeviceTelemetry } from '../services/DeviceService';
import { saveAccessToken } from '../services/AuthService';
import { getDeviceSigningPublicKey, storeApprovalDeviceId } from '../services/DeviceSigningService';
import { startMfaRealtime } from '../services/RealtimeService';
import { BASE_URL, getApplicationHeaders } from '../config';

export default function DeviceEnrollmentScreen({ navigation }) {
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

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
          signingPublicKey: await getDeviceSigningPublicKey(),
        }),
      });
      const data = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(data.detail ?? data.title ?? 'Phone setup failed.');
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
        <Text style={styles.title}>Enroll your first phone</Text>
        <Text style={styles.description}>For local development setup only. Enter your account credentials to bind this phone as the initial trusted MFA device.</Text>
        <Text style={styles.label}>Username</Text>
        <TextInput style={styles.input} autoCapitalize="none" value={username} onChangeText={setUsername} editable={!busy} />
        <Text style={styles.label}>Password</Text>
        <TextInput style={styles.input} secureTextEntry value={password} onChangeText={setPassword} editable={!busy} />
        {error ? <Text accessibilityRole="alert" style={styles.error}>{error}</Text> : null}
        <TouchableOpacity style={[styles.button, busy && styles.disabled]} onPress={enroll} disabled={busy}>
          {busy ? <ActivityIndicator color="#fff" /> : <Text style={styles.buttonText}>Enroll and sign in</Text>}
        </TouchableOpacity>
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
