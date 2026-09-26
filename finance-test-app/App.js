import React, { useEffect, useRef, useState } from 'react';
import {
  ActivityIndicator,
  KeyboardAvoidingView,
  Modal,
  Platform,
  Pressable,
  SafeAreaView,
  ScrollView,
  StatusBar,
  StyleSheet,
  Text,
  TextInput,
  View,
} from 'react-native';

const API_BASE_URL = (
  process.env.EXPO_PUBLIC_API_BASE_URL ||
  (Platform.OS === 'android' ? 'http://10.0.2.2:5143' : 'http://localhost:5143')
).replace(/\/+$/, '');

const FINANCIAL_DATA = [
  { label: 'Operating cash', value: '$4.82M', change: '+4.8%', color: '#167255' },
  { label: 'Quarterly revenue', value: '$1.26M', change: '+9.2%', color: '#167255' },
  { label: 'Open invoices', value: '284', change: '$318K total', color: '#b27a2b' },
  { label: 'Payroll forecast', value: '$742K', change: 'Due in 6 days', color: '#405a72' },
];

async function readResponse(response) {
  const body = await response.text();
  if (!body) return {};
  try {
    return JSON.parse(body);
  } catch {
    return { detail: body };
  }
}

function getErrorMessage(data, fallback) {
  return data.detail || data.title || data.message || fallback;
}

export default function App() {
  const [modal, setModal] = useState(null);
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [loginError, setLoginError] = useState('');
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [sessionToken, setSessionToken] = useState(null);
  const [mfaState, setMfaState] = useState('pending');
  const [mfaMessage, setMfaMessage] = useState('');
  const [isUnlocked, setIsUnlocked] = useState(false);
  const [signedInAs, setSignedInAs] = useState('');
  const requestController = useRef(null);

  useEffect(() => {
    if (!sessionToken) return undefined;

    let cancelled = false;
    const pollStatus = async () => {
      while (!cancelled) {
        const controller = new AbortController();
        requestController.current = controller;

        try {
          const response = await fetch(
            `${API_BASE_URL}/api/auth/mfa/status/${encodeURIComponent(sessionToken)}`,
            { headers: { Accept: 'application/json' }, cache: 'no-store', signal: controller.signal },
          );
          const data = await readResponse(response);

          if (cancelled) return;
          if (!response.ok && response.status !== 202) {
            setMfaState('error');
            setMfaMessage(getErrorMessage(data, `Status request failed (${response.status}).`));
            return;
          }

          const status = String(data.status || data.mfaStatus || '').toUpperCase();
          if (status === 'APPROVED') {
            if (!data.accessToken) {
              setMfaState('error');
              setMfaMessage('The session was approved, but the API did not return an access token.');
              return;
            }
            setSessionToken(null);
            setMfaState('approved');
            setModal(null);
            setIsUnlocked(true);
            return;
          }
          if (status === 'DENIED' || status === 'EXPIRED') {
            setMfaState(status.toLowerCase());
            setMfaMessage(status === 'DENIED'
              ? 'The sign-in was denied from your Safe Passage Security App.'
              : 'This approval request expired. Start a new sign-in to try again.');
            return;
          }
          if (status && status !== 'PENDING') {
            setMfaState('error');
            setMfaMessage(`Unexpected MFA status: ${status}.`);
            return;
          }

          setMfaMessage('');
        } catch (error) {
          if (cancelled || error.name === 'AbortError') return;
          setMfaMessage(`Reconnecting to Safe Passage at ${API_BASE_URL}...`);
        }

        await new Promise(resolve => setTimeout(resolve, 1000));
      }
    };

    void pollStatus();
    return () => {
      cancelled = true;
      requestController.current?.abort();
    };
  }, [sessionToken]);

  const openLogin = () => {
    setLoginError('');
    setModal('login');
  };

  const cancelMfa = () => {
    requestController.current?.abort();
    setSessionToken(null);
    setMfaState('pending');
    setMfaMessage('');
    setModal(null);
  };

  const submitLogin = async () => {
    if (!username.trim() || !password) {
      setLoginError('Enter your username and password to continue.');
      return;
    }

    setLoginError('');
    setIsSubmitting(true);
    try {
      const response = await fetch(`${API_BASE_URL}/api/auth/login`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
        body: JSON.stringify({ username: username.trim(), password }),
      });
      const data = await readResponse(response);

      if (!response.ok && response.status !== 202) {
        setLoginError(getErrorMessage(data, `Login failed (${response.status}).`));
        return;
      }

      setSignedInAs(username.trim());
      setModal('security');
      if (data.mfaRequired === false && data.accessToken) {
        setModal(null);
        setIsUnlocked(true);
        return;
      }
      if (!data.sessionToken) {
        setModal('login');
        setLoginError('The API response did not include an MFA session token.');
        return;
      }

      setMfaState('pending');
      setMfaMessage('');
      setSessionToken(data.sessionToken);
    } catch {
      setLoginError(`Could not reach ${API_BASE_URL}. Check the API address and that the backend is running.`);
    } finally {
      setIsSubmitting(false);
    }
  };

  const signOut = () => {
    requestController.current?.abort();
    setSessionToken(null);
    setIsUnlocked(false);
    setUsername('');
    setPassword('');
    setModal(null);
    setMfaState('pending');
    setMfaMessage('');
  };

  return (
    <SafeAreaView style={styles.safeArea}>
      <StatusBar barStyle="dark-content" />
      <View style={styles.appHeader}>
        <View style={styles.brandMark}><Text style={styles.brandLetters}>CF</Text></View>
        <View style={styles.brandTextWrap}>
          <Text style={styles.brandTitle}>Company Finance</Text>
          <Text style={styles.brandSubtitle}>TEST APP</Text>
        </View>
        <View style={styles.secureBadge}><View style={styles.secureDot} /><Text style={styles.secureLabel}>SAFE PASSAGE</Text></View>
      </View>

      {!isUnlocked ? (
        <ScrollView contentContainerStyle={styles.lockedContent}>
          <View style={styles.heroTopline}><Text style={styles.toplineText}>FINANCE OPERATIONS</Text><Text style={styles.toplineCode}>CF / 01</Text></View>
          <View style={styles.lockIcon}><Text style={styles.lockGlyph}>$</Text></View>
          <Text style={styles.heroTitle}>Confidential{ '\n' }finance data.</Text>
          <Text style={styles.heroCopy}>Company financial records are protected by Safe Passage device-based authentication.</Text>
          <View style={styles.protectedRow}>
            <View style={styles.protectedMark}><Text style={styles.protectedCheck}>✓</Text></View>
            <View style={styles.protectedCopy}><Text style={styles.protectedTitle}>Protected workspace</Text><Text style={styles.protectedSub}>Identity verification required</Text></View>
            <Text style={styles.protectedArrow}>›</Text>
          </View>
          <Pressable accessibilityRole="button" style={({ pressed }) => [styles.accessButton, pressed && styles.buttonPressed]} onPress={openLogin}>
            <Text style={styles.accessButtonText}>Access Confidential Finance Data</Text>
            <Text style={styles.accessButtonArrow}>→</Text>
          </Pressable>
          <Text style={styles.caption}>Secure access request · Safe Passage MFA</Text>
        </ScrollView>
      ) : (
        <ScrollView contentContainerStyle={styles.dashboardContent}>
          <View style={styles.dashboardHeading}>
            <View><Text style={styles.toplineText}>FINANCE OPERATIONS</Text><Text style={styles.dashboardTitle}>Overview</Text><Text style={styles.dashboardSub}>Welcome, {signedInAs || 'Finance user'}</Text></View>
            <Pressable accessibilityRole="button" style={styles.signOutButton} onPress={signOut}><Text style={styles.signOutText}>Sign out</Text></Pressable>
          </View>
          <View style={styles.approvedBanner}><View style={styles.approvedCheck}><Text style={styles.approvedCheckText}>✓</Text></View><View style={styles.approvedText}><Text style={styles.approvedTitle}>Identity verified</Text><Text style={styles.approvedSub}>Safe Passage access confirmed</Text></View><Text style={styles.approvedState}>SECURE</Text></View>
          <View style={styles.metricsGrid}>
            {FINANCIAL_DATA.map(item => (
              <View key={item.label} style={styles.metricCard}>
                <Text style={styles.metricLabel}>{item.label}</Text>
                <Text style={styles.metricValue}>{item.value}</Text>
                <Text style={[styles.metricChange, { color: item.color }]}>{item.change}</Text>
              </View>
            ))}
          </View>
          <View style={styles.activityCard}><View style={styles.activityHead}><Text style={styles.activityTitle}>Recent activity</Text><Text style={styles.activityPeriod}>TODAY</Text></View><View style={styles.activityLine}><View style={styles.activityDot} /><View style={styles.activityDetails}><Text style={styles.activityName}>Quarterly report exported</Text><Text style={styles.activityTime}>Finance team · 10:42 AM</Text></View><Text style={styles.activityAmount}>+$82,400</Text></View><View style={styles.activityLine}><View style={[styles.activityDot, styles.activityDotMuted]} /><View style={styles.activityDetails}><Text style={styles.activityName}>Payroll batch approved</Text><Text style={styles.activityTime}>Operations · 9:18 AM</Text></View><Text style={styles.activityAmount}>−$216,800</Text></View></View>
          <Text style={styles.mockNote}>Demonstration data only. No financial systems are connected.</Text>
        </ScrollView>
      )}

      <Modal visible={modal === 'login'} transparent animationType="fade" onRequestClose={() => setModal(null)}>
        <KeyboardAvoidingView style={styles.modalShade} behavior={Platform.OS === 'ios' ? 'padding' : undefined}>
          <View style={styles.loginModal}>
            <View style={styles.modalTopRow}><Text style={styles.modalEyebrow}>SAFE PASSAGE SIGN-IN</Text><Pressable accessibilityLabel="Close sign-in" onPress={() => setModal(null)} style={styles.closeButton}><Text style={styles.closeGlyph}>×</Text></Pressable></View>
            <Text style={styles.modalTitle}>Verify your identity</Text>
            <Text style={styles.modalCopy}>Enter your company credentials to request secure access.</Text>
            <Text style={styles.inputLabel}>USERNAME</Text>
            <TextInput value={username} onChangeText={setUsername} autoCapitalize="none" autoCorrect={false} autoComplete="username" textContentType="username" placeholder="Your username" placeholderTextColor="#8a9790" style={styles.textInput} returnKeyType="next" />
            <Text style={[styles.inputLabel, styles.passwordLabel]}>PASSWORD</Text>
            <TextInput value={password} onChangeText={setPassword} secureTextEntry autoComplete="password" textContentType="password" placeholder="Your password" placeholderTextColor="#8a9790" style={styles.textInput} onSubmitEditing={submitLogin} returnKeyType="go" />
            {loginError ? <Text accessibilityRole="alert" style={styles.loginError}>{loginError}</Text> : null}
            <Pressable accessibilityRole="button" disabled={isSubmitting} style={({ pressed }) => [styles.modalSubmit, pressed && styles.buttonPressed, isSubmitting && styles.buttonDisabled]} onPress={submitLogin}>
              {isSubmitting ? <ActivityIndicator color="#fff" size="small" /> : <Text style={styles.modalSubmitText}>Continue securely</Text>}
            </Pressable>
            <Text style={styles.modalFootnote}>Your password is sent only to the configured Safe Passage API.</Text>
          </View>
        </KeyboardAvoidingView>
      </Modal>

      <Modal visible={modal === 'security' || mfaState === 'denied' || mfaState === 'expired' || mfaState === 'error'} animationType="fade" presentationStyle="fullScreen" onRequestClose={cancelMfa}>
        <SafeAreaView style={styles.securityScreen}>
          <View style={styles.securityHeader}><View style={styles.brandMark}><Text style={styles.brandLetters}>CF</Text></View><Text style={styles.securityHeaderText}>Company Finance</Text><Text style={styles.securityTag}>SECURITY CHECK</Text></View>
          <View style={styles.securityBody}>
            {mfaState === 'pending' ? (
              <>
                <View style={styles.spinnerHalo}><ActivityIndicator size="large" color="#177557" /></View>
                <Text style={styles.securityEyebrow}>SAFE PASSAGE MFA</Text>
                <Text style={styles.securityTitle}>Security Check: Approve this login on your Safe Passage Security App.</Text>
                <Text style={styles.securityCopy}>We sent an approval request to your registered device. This screen will update automatically after you respond.</Text>
                {mfaMessage ? <Text style={styles.reconnectMessage}>{mfaMessage}</Text> : null}
                <View style={styles.waitingStatus}><View style={styles.waitingDot} /><Text style={styles.waitingText}>Waiting for confirmation</Text></View>
              </>
            ) : (
              <>
                <View style={[styles.resultMark, mfaState === 'error' ? styles.resultMarkError : null]}><Text style={[styles.resultGlyph, mfaState === 'error' ? styles.resultGlyphError : null]}>{mfaState === 'denied' ? '×' : mfaState === 'expired' ? '!' : '×'}</Text></View>
                <Text style={styles.securityEyebrow}>ACCESS NOT GRANTED</Text>
                <Text style={styles.securityTitle}>Authentication Blocked</Text>
                <Text style={styles.securityCopy}>{mfaMessage || 'This sign-in request could not be approved.'}</Text>
                <Pressable accessibilityRole="button" style={styles.modalSubmit} onPress={cancelMfa}><Text style={styles.modalSubmitText}>Return to finance app</Text></Pressable>
              </>
            )}
          </View>
          {mfaState === 'pending' ? <Pressable accessibilityRole="button" style={styles.cancelMfa} onPress={cancelMfa}><Text style={styles.cancelMfaText}>Cancel sign-in</Text></Pressable> : null}
          <Text style={styles.securityFooter}>CONFIDENTIAL ACCESS · {API_BASE_URL.replace(/^https?:\/\//, '')}</Text>
        </SafeAreaView>
      </Modal>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  safeArea: { flex: 1, backgroundColor: '#f1f4f1' },
  appHeader: { minHeight: 68, paddingHorizontal: 21, flexDirection: 'row', alignItems: 'center', borderBottomWidth: 1, borderBottomColor: '#dce4de', backgroundColor: '#f8faf8' },
  brandMark: { width: 38, height: 38, alignItems: 'center', justifyContent: 'center', borderRadius: 8, backgroundColor: '#104b3c' },
  brandLetters: { color: '#fff', fontSize: 12, fontWeight: '800', letterSpacing: 1 },
  brandTextWrap: { marginLeft: 10 },
  brandTitle: { color: '#1c2a23', fontSize: 13, fontWeight: '700' },
  brandSubtitle: { marginTop: 3, color: '#708078', fontSize: 9, fontWeight: '700', letterSpacing: 1.5 },
  secureBadge: { marginLeft: 'auto', flexDirection: 'row', alignItems: 'center', gap: 6, paddingHorizontal: 9, paddingVertical: 7, borderWidth: 1, borderColor: '#d6e0d8', borderRadius: 4 },
  secureDot: { width: 6, height: 6, borderRadius: 3, backgroundColor: '#167255' },
  secureLabel: { color: '#63736a', fontSize: 8, fontWeight: '800', letterSpacing: 1 },
  lockedContent: { flexGrow: 1, justifyContent: 'center', paddingHorizontal: 25, paddingVertical: 34 },
  heroTopline: { flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center', marginBottom: 27 },
  toplineText: { color: '#187557', fontSize: 9, fontWeight: '800', letterSpacing: 1.8 },
  toplineCode: { color: '#89958e', fontSize: 9, fontWeight: '700', letterSpacing: 1 },
  lockIcon: { width: 48, height: 48, alignItems: 'center', justifyContent: 'center', borderWidth: 1, borderColor: '#cfddd3', borderRadius: 8, backgroundColor: '#e8f1eb' },
  lockGlyph: { color: '#176d52', fontSize: 23, fontWeight: '500' },
  heroTitle: { marginTop: 19, color: '#18261f', fontSize: 35, fontWeight: '600', letterSpacing: -0.7, lineHeight: 41 },
  heroCopy: { maxWidth: 340, marginTop: 12, color: '#66746b', fontSize: 13, lineHeight: 20 },
  protectedRow: { minHeight: 69, flexDirection: 'row', alignItems: 'center', marginTop: 29, paddingHorizontal: 13, borderWidth: 1, borderColor: '#dce4de', borderRadius: 7, backgroundColor: '#f8faf8' },
  protectedMark: { width: 31, height: 31, alignItems: 'center', justifyContent: 'center', borderRadius: 6, backgroundColor: '#e4f1e9' },
  protectedCheck: { color: '#147255', fontSize: 16, fontWeight: '800' },
  protectedCopy: { flex: 1, marginLeft: 10 },
  protectedTitle: { color: '#2b3b31', fontSize: 11, fontWeight: '700' },
  protectedSub: { marginTop: 4, color: '#77837b', fontSize: 10 },
  protectedArrow: { color: '#708078', fontSize: 22 },
  accessButton: { minHeight: 53, flexDirection: 'row', alignItems: 'center', justifyContent: 'center', marginTop: 21, paddingHorizontal: 16, borderRadius: 6, backgroundColor: '#104b3c' },
  buttonPressed: { opacity: 0.86, transform: [{ scale: 0.99 }] },
  accessButtonText: { color: '#fff', fontSize: 12, fontWeight: '700' },
  accessButtonArrow: { marginLeft: 9, color: '#d5e8dd', fontSize: 17 },
  caption: { marginTop: 12, color: '#89948d', fontSize: 9, textAlign: 'center' },
  dashboardContent: { flexGrow: 1, paddingHorizontal: 20, paddingTop: 27, paddingBottom: 36 },
  dashboardHeading: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', marginBottom: 21 },
  dashboardTitle: { marginTop: 7, color: '#1a2921', fontSize: 27, fontWeight: '600' },
  dashboardSub: { marginTop: 4, color: '#728078', fontSize: 11 },
  signOutButton: { paddingHorizontal: 12, paddingVertical: 8, borderWidth: 1, borderColor: '#cfdbd2', borderRadius: 5 },
  signOutText: { color: '#42574a', fontSize: 10, fontWeight: '700' },
  approvedBanner: { minHeight: 65, flexDirection: 'row', alignItems: 'center', paddingHorizontal: 13, borderWidth: 1, borderColor: '#cfe2d4', borderRadius: 6, backgroundColor: '#edf6ef' },
  approvedCheck: { width: 29, height: 29, alignItems: 'center', justifyContent: 'center', borderRadius: 15, backgroundColor: '#d9eddf' },
  approvedCheckText: { color: '#167255', fontSize: 15, fontWeight: '800' },
  approvedText: { flex: 1, marginLeft: 10 },
  approvedTitle: { color: '#24583d', fontSize: 11, fontWeight: '700' },
  approvedSub: { marginTop: 3, color: '#64806e', fontSize: 9 },
  approvedState: { color: '#31734e', fontSize: 8, fontWeight: '800', letterSpacing: 1 },
  metricsGrid: { flexDirection: 'row', flexWrap: 'wrap', gap: 11, marginTop: 17 },
  metricCard: { width: '47%', minHeight: 112, justifyContent: 'center', padding: 13, borderWidth: 1, borderColor: '#e0e7e1', borderRadius: 6, backgroundColor: '#fff' },
  metricLabel: { color: '#6e7c73', fontSize: 9, fontWeight: '600' },
  metricValue: { marginTop: 9, color: '#203127', fontSize: 21, fontWeight: '600' },
  metricChange: { marginTop: 6, fontSize: 9, fontWeight: '600' },
  activityCard: { marginTop: 17, paddingHorizontal: 14, borderWidth: 1, borderColor: '#e0e7e1', borderRadius: 6, backgroundColor: '#fff' },
  activityHead: { minHeight: 46, flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', borderBottomWidth: 1, borderBottomColor: '#edf0ed' },
  activityTitle: { color: '#27382d', fontSize: 11, fontWeight: '700' },
  activityPeriod: { color: '#859087', fontSize: 8, fontWeight: '700', letterSpacing: 1 },
  activityLine: { minHeight: 59, flexDirection: 'row', alignItems: 'center', borderBottomWidth: 1, borderBottomColor: '#edf0ed' },
  activityDot: { width: 7, height: 7, borderRadius: 4, backgroundColor: '#25845e' },
  activityDotMuted: { backgroundColor: '#b27a2b' },
  activityDetails: { flex: 1, marginLeft: 10 },
  activityName: { color: '#394a3f', fontSize: 10, fontWeight: '600' },
  activityTime: { marginTop: 4, color: '#879189', fontSize: 8 },
  activityAmount: { color: '#4e5f54', fontSize: 9, fontWeight: '700' },
  mockNote: { marginTop: 13, color: '#849087', fontSize: 9, textAlign: 'center' },
  modalShade: { flex: 1, justifyContent: 'center', padding: 20, backgroundColor: 'rgba(17, 35, 27, 0.54)' },
  loginModal: { padding: 22, borderRadius: 9, backgroundColor: '#fff' },
  modalTopRow: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between' },
  modalEyebrow: { color: '#177255', fontSize: 9, fontWeight: '800', letterSpacing: 1.5 },
  closeButton: { width: 30, height: 30, alignItems: 'center', justifyContent: 'center', borderRadius: 15, backgroundColor: '#f0f3f0' },
  closeGlyph: { marginTop: -2, color: '#66736b', fontSize: 22 },
  modalTitle: { marginTop: 14, color: '#1b2b21', fontSize: 22, fontWeight: '600' },
  modalCopy: { marginTop: 7, marginBottom: 21, color: '#6c7971', fontSize: 11, lineHeight: 17 },
  inputLabel: { marginBottom: 7, color: '#4e5e53', fontSize: 9, fontWeight: '800', letterSpacing: 1 },
  textInput: { height: 46, paddingHorizontal: 12, borderWidth: 1, borderColor: '#d0dad2', borderRadius: 5, backgroundColor: '#fff', color: '#1c2a22', fontSize: 12 },
  passwordLabel: { marginTop: 15 },
  loginError: { marginTop: 10, color: '#a13f39', fontSize: 10, lineHeight: 15 },
  modalSubmit: { minHeight: 47, alignItems: 'center', justifyContent: 'center', marginTop: 19, borderRadius: 5, backgroundColor: '#104b3c' },
  buttonDisabled: { opacity: 0.7 },
  modalSubmitText: { color: '#fff', fontSize: 11, fontWeight: '700' },
  modalFootnote: { marginTop: 14, color: '#89948d', fontSize: 9, lineHeight: 14, textAlign: 'center' },
  securityScreen: { flex: 1, backgroundColor: '#f4f6f3' },
  securityHeader: { minHeight: 70, flexDirection: 'row', alignItems: 'center', paddingHorizontal: 20, borderBottomWidth: 1, borderBottomColor: '#dfe6df', backgroundColor: '#fff' },
  securityHeaderText: { marginLeft: 10, color: '#25362b', fontSize: 12, fontWeight: '700' },
  securityTag: { marginLeft: 'auto', color: '#668073', fontSize: 8, fontWeight: '800', letterSpacing: 1.1 },
  securityBody: { flex: 1, alignItems: 'center', justifyContent: 'center', paddingHorizontal: 30 },
  spinnerHalo: { width: 76, height: 76, alignItems: 'center', justifyContent: 'center', marginBottom: 25, borderRadius: 40, backgroundColor: '#e4efe8' },
  securityEyebrow: { marginBottom: 11, color: '#187557', fontSize: 9, fontWeight: '800', letterSpacing: 1.6, textAlign: 'center' },
  securityTitle: { maxWidth: 340, color: '#1a2c21', fontSize: 22, fontWeight: '600', lineHeight: 30, textAlign: 'center' },
  securityCopy: { maxWidth: 320, marginTop: 11, color: '#68766d', fontSize: 11, lineHeight: 18, textAlign: 'center' },
  reconnectMessage: { marginTop: 14, color: '#9b6927', fontSize: 10, lineHeight: 15, textAlign: 'center' },
  waitingStatus: { flexDirection: 'row', alignItems: 'center', gap: 7, marginTop: 25, paddingHorizontal: 11, paddingVertical: 8, borderRadius: 4, backgroundColor: '#eaf1eb' },
  waitingDot: { width: 6, height: 6, borderRadius: 4, backgroundColor: '#bc8439' },
  waitingText: { color: '#596a5e', fontSize: 9, fontWeight: '600' },
  cancelMfa: { alignSelf: 'center', marginBottom: 23, padding: 10 },
  cancelMfaText: { color: '#617268', fontSize: 10, textDecorationLine: 'underline' },
  securityFooter: { paddingHorizontal: 20, paddingBottom: 13, color: '#94a098', fontSize: 8, fontWeight: '700', letterSpacing: 1, textAlign: 'center' },
  resultMark: { width: 62, height: 62, alignItems: 'center', justifyContent: 'center', marginBottom: 23, borderRadius: 31, backgroundColor: '#fae9e6' },
  resultMarkError: { backgroundColor: '#fae9e6' },
  resultGlyph: { marginTop: -3, color: '#aa403b', fontSize: 36, fontWeight: '400' },
  resultGlyphError: { color: '#aa403b' },
});
