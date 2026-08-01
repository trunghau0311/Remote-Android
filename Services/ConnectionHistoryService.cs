using System.Text.Json;
using MauiRemoteAdb.Models;

namespace MauiRemoteAdb.Services;

/// <summary>
/// Lưu lịch sử kết nối (IP/domain + port) vào bộ nhớ local của app (Preferences —
/// đơn giản, đủ dùng cho danh sách ngắn, không cần database thật). Giữ tối đa
/// <see cref="MaxItems"/> mục gần nhất, mục dùng gần đây nhất luôn lên đầu.
/// </summary>
public class ConnectionHistoryService
{
    private const string StorageKey = "connection_history_v1";
    private const int MaxItems = 15;

    public List<ConnectionHistoryItem> Load()
    {
        try
        {
            string json = Preferences.Default.Get(StorageKey, string.Empty);
            if (string.IsNullOrWhiteSpace(json)) return new List<ConnectionHistoryItem>();

            var items = JsonSerializer.Deserialize<List<ConnectionHistoryItem>>(json);
            return items ?? new List<ConnectionHistoryItem>();
        }
        catch
        {
            // Dữ liệu lưu trước đó có thể hỏng/sai định dạng (ví dụ đổi version app) —
            // trả về danh sách rỗng thay vì crash.
            return new List<ConnectionHistoryItem>();
        }
    }

    private void Save(List<ConnectionHistoryItem> items)
    {
        string json = JsonSerializer.Serialize(items);
        Preferences.Default.Set(StorageKey, json);
    }

    /// <summary>Thêm/cập nhật 1 mục — nếu Host:Port đã có sẵn, chỉ cập nhật thời gian dùng gần nhất và đưa lên đầu.</summary>
    public void AddOrUpdate(string host, string port)
    {
        var items = Load();

        items.RemoveAll(i =>
            string.Equals(i.Host, host, StringComparison.OrdinalIgnoreCase) &&
            i.Port == port);

        items.Insert(0, new ConnectionHistoryItem { Host = host, Port = port, LastUsed = DateTime.Now });

        if (items.Count > MaxItems)
            items = items.Take(MaxItems).ToList();

        Save(items);
    }

    public void Remove(ConnectionHistoryItem item)
    {
        var items = Load();
        items.RemoveAll(i =>
            string.Equals(i.Host, item.Host, StringComparison.OrdinalIgnoreCase) &&
            i.Port == item.Port);
        Save(items);
    }

    public void Clear() => Preferences.Default.Remove(StorageKey);
}
