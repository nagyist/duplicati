// Copyright (C) 2025, The Duplicati Team
// https://duplicati.com, hello@duplicati.com
// 
// Permission is hereby granted, free of charge, to any person obtaining a 
// copy of this software and associated documentation files (the "Software"), 
// to deal in the Software without restriction, including without limitation 
// the rights to use, copy, modify, merge, publish, distribute, sublicense, 
// and/or sell copies of the Software, and to permit persons to whom the 
// Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in 
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS 
// OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, 
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE 
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER 
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING 
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER 
// DEALINGS IN THE SOFTWARE.
namespace Duplicati.Library.RestAPI;

public interface INotificationUpdateService
{
    /// <summary>
    /// An event ID that increases whenever the database is updated
    /// </summary>
    long LastDataUpdateId { get; }

    /// <summary>
    /// An event ID that increases whenever a notification is updated
    /// </summary>
    long LastNotificationUpdateId { get; }

    void IncrementLastDataUpdateId();
    void IncrementLastNotificationUpdateId();
}

public class NotificationUpdateService : INotificationUpdateService
{
    /// <summary>
    /// An event ID that increases whenever the database is updated
    /// </summary>
    public long LastDataUpdateId { get; private set; } = 0;

    private readonly object _lastDataUpdateIdLock = new();

    /// <summary>
    /// An event ID that increases whenever a notification is updated
    /// </summary>
    public long LastNotificationUpdateId { get; private set; } = 0;

    private readonly object _lastNotificationUpdateIdLock = new();

    public void IncrementLastDataUpdateId()
    {
        lock (_lastDataUpdateIdLock)
        {
            LastDataUpdateId++;
        }
    }    
    
    public void IncrementLastNotificationUpdateId()
    {
        lock (_lastNotificationUpdateIdLock)
        {
            LastNotificationUpdateId++;
        }
    }
}