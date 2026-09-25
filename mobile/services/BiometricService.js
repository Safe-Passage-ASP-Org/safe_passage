import * as LocalAuthentication from 'expo-local-authentication';

/**
 * Checks if the device supports biometric authentication and has enrolled credentials.
 * Returns a structured capability object.
 */
export async function checkBiometricCapability() {
  const isHardwareAvailable = await LocalAuthentication.hasHardwareAsync();
  const isEnrolled = await LocalAuthentication.isEnrolledAsync();
  const supportedTypes = await LocalAuthentication.supportedAuthenticationTypesAsync();

  const typeNames = supportedTypes.map((type) => {
    switch (type) {
      case LocalAuthentication.AuthenticationType.FINGERPRINT:
        return 'Fingerprint';
      case LocalAuthentication.AuthenticationType.FACIAL_RECOGNITION:
        return 'Face ID';
      case LocalAuthentication.AuthenticationType.IRIS:
        return 'Iris';
      default:
        return 'Unknown';
    }
  });

  return {
    isAvailable: isHardwareAvailable && isEnrolled,
    isHardwareAvailable,
    isEnrolled,
    supportedTypes: typeNames,
  };
}

/**
 * Prompts the user for biometric authentication.
 * @param {string} reason - The reason string shown on the biometric prompt.
 * @returns {{ success: boolean, error?: string }}
 */
export async function authenticateWithBiometrics(reason = 'Confirm your identity to approve this sign-in request.') {
  const capability = await checkBiometricCapability();

  if (!capability.isAvailable) {
    if (!capability.isHardwareAvailable) {
      return { success: false, error: 'NO_HARDWARE', message: 'This device does not support biometric authentication.' };
    }
    if (!capability.isEnrolled) {
      return { success: false, error: 'NOT_ENROLLED', message: 'No biometrics enrolled. Please set up Face ID or Fingerprint in your device settings.' };
    }
  }

  const result = await LocalAuthentication.authenticateAsync({
    promptMessage: reason,
    cancelLabel: 'Cancel',
    disableDeviceFallback: false, // Allow PIN fallback
    fallbackLabel: 'Use PIN',
  });

  if (result.success) {
    return { success: true };
  }

  // Map expo error codes to friendly messages
  let message = 'Biometric authentication failed. Please try again.';
  if (result.error === 'user_cancel') {
    message = 'Authentication cancelled.';
  } else if (result.error === 'lockout' || result.error === 'lockout_permanent') {
    message = 'Too many failed attempts. Device is locked. Use your PIN.';
  }

  return { success: false, error: result.error, message };
}
