import React, { useCallback, useState } from 'react';
import { ActivityIndicator, FlatList, Pressable, StyleSheet, Text, View } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { useFocusEffect } from '@react-navigation/native';
import { getToken } from '../services/AuthService';
import { getApprovalDeviceId } from '../services/DeviceSigningService';
import { BASE_URL, getApplicationHeaders } from '../config';

export default function NotificationsScreen() {
  const [notifications, setNotifications] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [refresh, setRefresh] = useState(0);

  const loadNotifications = useCallback(async (active) => {
    try {
      const token = await getToken();
      const deviceId = await getApprovalDeviceId();
      if (!token || !deviceId) throw new Error('Sign in on an enrolled device.');
      const response = await fetch(`${BASE_URL}/api/notifications`, {
        headers: { Authorization: `Bearer ${token}`, ...getApplicationHeaders(deviceId) },
      });
      if (!response.ok) throw new Error(response.statusText);
      const data = await response.json();
      if (active.current) {
        setNotifications(Array.isArray(data) ? data : []);
        setError('');
      }
    } catch {
      if (active.current) setError('Failed to load notifications. Check your connection.');
    } finally {
      if (active.current) setLoading(false);
    }
  }, []);

  useFocusEffect(useCallback(() => {
    const active = { current: true };
    setLoading(true);
    loadNotifications(active);
    return () => { active.current = false; };
  }, [loadNotifications, refresh]));

  const markRead = async (item) => {
    if (item.readAt) return;
    try {
      const token = await getToken();
      const deviceId = await getApprovalDeviceId();
      const response = await fetch(`${BASE_URL}/api/notifications/${item.id}/read`, {
        method: 'POST',
        headers: { Authorization: `Bearer ${token}`, ...getApplicationHeaders(deviceId) },
      });
      if (!response.ok) throw new Error(response.statusText);
      setNotifications((current) => current.map((notification) => notification.id === item.id
        ? { ...notification, readAt: new Date().toISOString() }
        : notification));
    } catch {
      setError('Could not mark this notification as read. Try again.');
    }
  };

  const markAllRead = async () => {
    try {
      const token = await getToken();
      const deviceId = await getApprovalDeviceId();
      const response = await fetch(`${BASE_URL}/api/notifications/read-all`, {
        method: 'POST',
        headers: { Authorization: `Bearer ${token}`, ...getApplicationHeaders(deviceId) },
      });
      if (!response.ok) throw new Error(response.statusText);
      const readAt = new Date().toISOString();
      setNotifications((current) => current.map((item) => ({ ...item, readAt: item.readAt ?? readAt })));
    } catch {
      setError('Could not mark notifications as read. Try again.');
    }
  };

  const renderItem = ({ item }) => (
    <Pressable onPress={() => markRead(item)} style={[styles.item, !item.readAt && styles.unread]}>
      <View style={styles.icon}>
        <Ionicons name={item.category === 'Security' ? 'shield-checkmark' : 'notifications'} size={19} color="#2dd4bf" />
      </View>
      <View style={styles.copy}>
        <Text style={styles.category}>{item.category}</Text>
        <Text style={styles.title}>{item.title}</Text>
        <Text style={styles.message}>{item.message}</Text>
        <Text style={styles.time}>{new Date(item.createdAt).toLocaleString()}</Text>
      </View>
      {!item.readAt ? <View style={styles.dot} /> : null}
    </Pressable>
  );

  const unreadCount = notifications.filter((item) => !item.readAt).length;
  return (
    <View style={styles.container}>
      <View style={styles.toolbar}>
        <Text style={styles.unreadCount}>{unreadCount} unread</Text>
        <View style={styles.actions}>
          <Pressable onPress={() => setRefresh((value) => value + 1)} style={styles.actionButton}>
            <Text style={styles.actionText}>Refresh</Text>
          </Pressable>
          {unreadCount > 0 ? (
            <Pressable onPress={markAllRead} style={styles.actionButton}>
              <Text style={styles.actionText}>Mark all read</Text>
            </Pressable>
          ) : null}
        </View>
      </View>
      {error ? <Text style={styles.error}>{error}</Text> : null}
      {loading ? <ActivityIndicator color="#0ea5e9" size="large" style={styles.loading} /> : null}
      {!loading && !error && notifications.length === 0 ? <Text style={styles.empty}>You’re all caught up.</Text> : null}
      {!loading && notifications.length > 0 ? (
        <FlatList
          data={notifications}
          keyExtractor={(item) => item.id}
          renderItem={renderItem}
          contentContainerStyle={styles.list}
        />
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: '#020617', padding: 16 },
  toolbar: { flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center', marginBottom: 12 },
  unreadCount: { color: '#94a3b8', fontSize: 13 },
  actions: { flexDirection: 'row', gap: 12 },
  actionButton: { paddingVertical: 7, paddingHorizontal: 10, borderWidth: 1, borderColor: '#334155', borderRadius: 7 },
  actionText: { color: '#cbd5e1', fontSize: 12 },
  list: { gap: 9, paddingBottom: 20 },
  item: { flexDirection: 'row', alignItems: 'center', gap: 12, padding: 13, borderRadius: 12, borderWidth: 1, borderColor: '#1e293b', backgroundColor: '#0f172a' },
  unread: { borderLeftWidth: 3, borderLeftColor: '#2dd4bf' },
  icon: { width: 36, height: 36, borderRadius: 18, backgroundColor: '#134e4a', alignItems: 'center', justifyContent: 'center' },
  copy: { flex: 1, gap: 3 },
  category: { color: '#2dd4bf', fontSize: 10, fontWeight: '700', textTransform: 'uppercase' },
  title: { color: '#f8fafc', fontSize: 14, fontWeight: '600' },
  message: { color: '#cbd5e1', fontSize: 12 },
  time: { color: '#64748b', fontSize: 10, marginTop: 3 },
  dot: { width: 8, height: 8, borderRadius: 4, backgroundColor: '#2dd4bf' },
  loading: { marginTop: 40 },
  error: { color: '#f87171', padding: 12, textAlign: 'center' },
  empty: { color: '#64748b', textAlign: 'center', marginTop: 30 },
});
