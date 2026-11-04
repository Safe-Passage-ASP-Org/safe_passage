import React, { useState, useCallback } from 'react';
import { View, Text, StyleSheet, FlatList, ActivityIndicator } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { useFocusEffect } from '@react-navigation/native';
import { getToken } from '../services/AuthService';
import { getApprovalDeviceId } from '../services/DeviceSigningService';
import { BASE_URL, getApplicationHeaders } from '../config';

export default function ActivityScreen() {
  const [activity, setActivity] = useState([]);
  const [loading, setLoading]   = useState(true);
  const [error, setError]       = useState('');

  useFocusEffect(useCallback(() => {
    let active = true;
    setActivity([]);
    setLoading(true);
    setError('');
    (async () => {
      try {
        const token = await getToken();
        const deviceId = await getApprovalDeviceId();
        if (!token || !deviceId) throw new Error('Sign in on an enrolled device.');
        const response = await fetch(`${BASE_URL}/api/activity`, {
          headers: { Authorization: `Bearer ${token}`, ...getApplicationHeaders(deviceId) },
        });
        if (!response.ok) throw new Error(response.statusText);
        const data = await response.json();
        if (active) setActivity(data);
      } catch (err) {
        if (active) setError('Failed to load activity. Check your connection.');
      } finally {
        if (active) setLoading(false);
      }
    })();
    return () => { active = false; };
  }, []));

  const renderItem = ({ item }) => (
    <View style={styles.activityItem}>
      <View style={[styles.iconContainer, { backgroundColor: `${item.color}20` }]}>
        <Ionicons name={item.icon} size={20} color={item.color} />
      </View>
      <View style={styles.activityInfo}>
        <Text style={styles.activityAction}>{item.action}</Text>
        <Text style={styles.activityTarget}>{item.target}</Text>
      </View>
      <Text style={styles.activityTime}>{item.time}</Text>
    </View>
  );

  return (
    <View style={styles.container}>
      <View style={styles.timelineContainer}>
        {loading && (
          <ActivityIndicator color="#0ea5e9" size="large" style={{ marginTop: 40 }} />
        )}
        {error ? (
          <Text style={styles.errorText}>{error}</Text>
        ) : null}
        {!loading && !error && activity.length === 0 && (
          <Text style={styles.emptyText}>No activity yet.</Text>
        )}
        {!loading && !error && activity.length > 0 && (
          <FlatList
            data={activity}
            keyExtractor={(item) => item.id}
            renderItem={renderItem}
            contentContainerStyle={styles.listContent}
          />
        )}
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    backgroundColor: '#020617',
  },
  timelineContainer: {
    flex: 1,
    backgroundColor: '#0f172a',
    margin: 16,
    borderRadius: 16,
    borderWidth: 1,
    borderColor: '#1e293b',
    overflow: 'hidden',
  },
  listContent: {
    padding: 16,
  },
  errorText: {
    color: '#ef4444',
    textAlign: 'center',
    marginTop: 20,
    fontSize: 15,
    padding: 16,
  },
  emptyText: {
    color: '#64748b',
    textAlign: 'center',
    marginTop: 20,
    fontSize: 15,
    padding: 16,
  },
  activityItem: {
    flexDirection: 'row',
    alignItems: 'center',
    paddingVertical: 16,
    borderBottomWidth: 1,
    borderBottomColor: '#1e293b',
  },
  iconContainer: {
    padding: 10,
    borderRadius: 10,
    marginRight: 16,
  },
  activityInfo: {
    flex: 1,
  },
  activityAction: {
    fontSize: 16,
    fontWeight: '500',
    color: '#f8fafc',
    marginBottom: 4,
  },
  activityTarget: {
    fontSize: 14,
    color: '#94a3b8',
  },
  activityTime: {
    fontSize: 12,
    color: '#64748b',
  },
});
