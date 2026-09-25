import React from 'react';
import { View, Text, StyleSheet, ScrollView, TouchableOpacity } from 'react-native';
import { Ionicons } from '@expo/vector-icons';

export default function DashboardScreen({ navigation }) {
  return (
    <ScrollView style={styles.container}>
      <View style={styles.statusCard}>
        <Ionicons name="shield-checkmark" size={48} color="#10b981" />
        <Text style={styles.statusTitle}>Security Status</Text>
        <Text style={styles.statusValue}>Protected</Text>
      </View>

      <View style={styles.section}>
        <Text style={styles.sectionTitle}>Pending Requests</Text>
        <TouchableOpacity style={styles.alertCard} onPress={() => navigation.navigate('MfaApproval')}>
          <View style={styles.alertIcon}>
            <Ionicons name="warning" size={24} color="#f59e0b" />
          </View>
          <View style={styles.alertContent}>
            <Text style={styles.alertTitle}>MFA Request</Text>
            <Text style={styles.alertDesc}>San Francisco, CA • Windows 11</Text>
          </View>
          <Ionicons name="chevron-forward" size={20} color="#64748b" />
        </TouchableOpacity>
      </View>

      <View style={styles.section}>
        <Text style={styles.sectionTitle}>Recent Alerts</Text>
        <View style={styles.card}>
          <View style={styles.alertRow}>
            <Ionicons name="checkmark-circle" size={24} color="#10b981" />
            <View style={styles.alertContent}>
              <Text style={styles.alertTitle}>New Device Registered</Text>
              <Text style={styles.alertDesc}>iPhone 13 Pro • 2 hours ago</Text>
            </View>
          </View>
          <View style={[styles.alertRow, styles.lastAlertRow]}>
            <Ionicons name="close-circle" size={24} color="#ef4444" />
            <View style={styles.alertContent}>
              <Text style={styles.alertTitle}>Blocked Sign-in Attempt</Text>
              <Text style={styles.alertDesc}>Moscow, RU • Yesterday</Text>
            </View>
          </View>
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
  statusTitle: {
    fontSize: 16,
    color: '#94a3b8',
    marginTop: 12,
  },
  statusValue: {
    fontSize: 28,
    fontWeight: 'bold',
    color: '#10b981',
    marginTop: 4,
  },
  section: {
    marginBottom: 24,
  },
  sectionTitle: {
    fontSize: 18,
    fontWeight: '600',
    color: '#f8fafc',
    marginBottom: 12,
  },
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
  alertContent: {
    flex: 1,
  },
  alertTitle: {
    fontSize: 16,
    fontWeight: '500',
    color: '#f8fafc',
  },
  alertDesc: {
    fontSize: 14,
    color: '#94a3b8',
    marginTop: 2,
  },
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
  },
  lastAlertRow: {
    borderBottomWidth: 0,
  },
});
