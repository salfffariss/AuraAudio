using System;
using System.Collections.Generic;
using System.Linq;
using AuraAudio.Core.Models;

namespace AuraAudio.Audio.Queue;

/// <summary>
/// Pengelola Antrean Pemutaran Audio & Algoritma Acak Fisher-Yates (Playback Queue & Shuffle Engine).
/// Fitur Utama:
/// 1. Algoritma Fisher-Yates Shuffle murni tanpa duplikasi lagu berulang.
/// 2. Mode Perulangan (Off, Repeat All, Repeat One).
/// 3. Navigasi Previous akurat berbasis Stack History (bahkan saat dalam mode acak).
/// 4. Manipulasi antrean dinamis (Play Next, Append, Drag-and-Drop Move, Remove).
/// 5. Kompatibel dengan persistensi sesi SQLite.
/// </summary>
public class PlaybackQueueManager
{
    private readonly object _lock = new();
    private readonly List<Track> _originalList = new();
    private readonly List<int> _shuffledIndices = new();
    private readonly Stack<int> _history = new();

    private int _currentOriginalIndex = -1;
    private PlaybackRepeatMode _repeatMode = PlaybackRepeatMode.Off;
    private bool _isShuffleEnabled = false;

    public Track? CurrentTrack
    {
        get
        {
            lock (_lock)
            {
                if (_currentOriginalIndex >= 0 && _currentOriginalIndex < _originalList.Count)
                {
                    return _originalList[_currentOriginalIndex];
                }
                return null;
            }
        }
    }

    /// <summary>
    /// Daftar lagu dalam antrean sesuai urutan tampilan saat ini (linear atau shuffled).
    /// </summary>
    public IReadOnlyList<Track> Items
    {
        get
        {
            lock (_lock)
            {
                if (!_isShuffleEnabled) return _originalList.ToList();
                return _shuffledIndices.Select(idx => _originalList[idx]).ToList();
            }
        }
    }

    public int Count { get { lock (_lock) return _originalList.Count; } }
    public int CurrentIndex => _currentOriginalIndex;
    public PlaybackRepeatMode RepeatMode { get => _repeatMode; set { lock (_lock) _repeatMode = value; } }
    public bool IsShuffleEnabled => _isShuffleEnabled;
    public bool CanGoPrevious => _history.Count > 0 || _currentOriginalIndex > 0;

    /// <summary>
    /// Menetapkan antrean lagu baru dan menginisialisasi indeks lagu awal.
    /// </summary>
    public void SetQueue(IEnumerable<Track> tracks, int startIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        lock (_lock)
        {
            _originalList.Clear();
            _originalList.AddRange(tracks);
            _history.Clear();

            if (_originalList.Count == 0)
            {
                _currentOriginalIndex = -1;
                _shuffledIndices.Clear();
                return;
            }

            _currentOriginalIndex = Math.Clamp(startIndex, 0, _originalList.Count - 1);

            if (_isShuffleEnabled)
            {
                RegenerateShuffleIndices(keepFirstIndex: _currentOriginalIndex);
            }
        }
    }

    /// <summary>
    /// Menyisipkan lagu agar diputar tepat setelah lagu saat ini (Play Next).
    /// </summary>
    public void PlayNext(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);

        lock (_lock)
        {
            int insertIndex = _currentOriginalIndex + 1;
            if (insertIndex > _originalList.Count) insertIndex = _originalList.Count;

            _originalList.Insert(insertIndex, track);

            if (_isShuffleEnabled)
            {
                RegenerateShuffleIndices(keepFirstIndex: _currentOriginalIndex);
            }
        }
    }

    /// <summary>
    /// Menambahkan lagu ke urutan paling akhir antrean (Add to Queue).
    /// </summary>
    public void Add(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);

        lock (_lock)
        {
            _originalList.Add(track);

            if (_currentOriginalIndex < 0)
            {
                _currentOriginalIndex = 0;
            }

            if (_isShuffleEnabled)
            {
                RegenerateShuffleIndices(keepFirstIndex: _currentOriginalIndex);
            }
        }
    }

    /// <summary>
    /// Menghapus lagu dari antrean pada indeks tertentu.
    /// </summary>
    public bool RemoveAt(int index)
    {
        lock (_lock)
        {
            if (index < 0 || index >= _originalList.Count) return false;

            _originalList.RemoveAt(index);

            if (_currentOriginalIndex >= _originalList.Count)
            {
                _currentOriginalIndex = _originalList.Count - 1;
            }

            if (_isShuffleEnabled)
            {
                RegenerateShuffleIndices(keepFirstIndex: _currentOriginalIndex);
            }

            return true;
        }
    }

    /// <summary>
    /// Mengatur ulang posisi urutan lagu di dalam antrean (Reorder / Drag-and-Drop).
    /// </summary>
    public void Move(int fromIndex, int toIndex)
    {
        lock (_lock)
        {
            if (fromIndex < 0 || toIndex < 0) return;
            if (fromIndex == toIndex) return;

            if (_isShuffleEnabled)
            {
                if (fromIndex >= _shuffledIndices.Count || toIndex >= _shuffledIndices.Count) return;

                int origIdx = _shuffledIndices[fromIndex];
                _shuffledIndices.RemoveAt(fromIndex);
                _shuffledIndices.Insert(toIndex, origIdx);
            }
            else
            {
                if (fromIndex >= _originalList.Count || toIndex >= _originalList.Count) return;

                var item = _originalList[fromIndex];
                _originalList.RemoveAt(fromIndex);
                _originalList.Insert(toIndex, item);

                // Perbarui posisi lagu aktif jika ikut berpindah
                if (_currentOriginalIndex == fromIndex)
                {
                    _currentOriginalIndex = toIndex;
                }
                else if (fromIndex < _currentOriginalIndex && toIndex >= _currentOriginalIndex)
                {
                    _currentOriginalIndex--;
                }
                else if (fromIndex > _currentOriginalIndex && toIndex <= _currentOriginalIndex)
                {
                    _currentOriginalIndex++;
                }
            }
        }
    }

    /// <summary>
    /// Mengaktifkan atau menonaktifkan mode acak (Shuffle).
    /// </summary>
    public void SetShuffle(bool enable)
    {
        lock (_lock)
        {
            if (_isShuffleEnabled == enable) return;
            _isShuffleEnabled = enable;

            if (_isShuffleEnabled)
            {
                RegenerateShuffleIndices(keepFirstIndex: _currentOriginalIndex);
            }
            else
            {
                _shuffledIndices.Clear();
            }
        }
    }

    /// <summary>
    /// Melihat lagu berikutnya tanpa memajukan posisi antrean (untuk Gapless Pre-Buffering).
    /// </summary>
    public Track? PeekNextTrack()
    {
        lock (_lock)
        {
            if (_originalList.Count == 0) return null;

            if (_repeatMode == PlaybackRepeatMode.RepeatOne)
            {
                return CurrentTrack;
            }

            if (!_isShuffleEnabled)
            {
                int next = _currentOriginalIndex + 1;
                if (next < _originalList.Count) return _originalList[next];
                if (_repeatMode == PlaybackRepeatMode.RepeatAll) return _originalList[0];
                return null;
            }
            else
            {
                int curShufflePos = _shuffledIndices.IndexOf(_currentOriginalIndex);
                if (curShufflePos >= 0 && curShufflePos + 1 < _shuffledIndices.Count)
                {
                    return _originalList[_shuffledIndices[curShufflePos + 1]];
                }
                if (_repeatMode == PlaybackRepeatMode.RepeatAll && _shuffledIndices.Count > 0)
                {
                    return _originalList[_shuffledIndices[0]];
                }
                return null;
            }
        }
    }

    /// <summary>
    /// Memajukan antrean ke lagu berikutnya dengan mempertimbangkan Repeat Mode dan Fisher-Yates Shuffle.
    /// </summary>
    public Track? AdvanceNext()
    {
        lock (_lock)
        {
            if (_originalList.Count == 0) return null;

            if (_repeatMode == PlaybackRepeatMode.RepeatOne)
            {
                return CurrentTrack;
            }

            if (_currentOriginalIndex >= 0)
            {
                _history.Push(_currentOriginalIndex);
            }

            if (!_isShuffleEnabled)
            {
                _currentOriginalIndex++;
                if (_currentOriginalIndex >= _originalList.Count)
                {
                    if (_repeatMode == PlaybackRepeatMode.RepeatAll)
                    {
                        _currentOriginalIndex = 0;
                    }
                    else
                    {
                        _currentOriginalIndex = _originalList.Count - 1;
                        return null; // Akhir antrean
                    }
                }
            }
            else
            {
                int curShufflePos = _shuffledIndices.IndexOf(_currentOriginalIndex);
                int nextShufflePos = curShufflePos + 1;

                if (nextShufflePos >= _shuffledIndices.Count)
                {
                    if (_repeatMode == PlaybackRepeatMode.RepeatAll)
                    {
                        RegenerateShuffleIndices();
                        nextShufflePos = 0;
                    }
                    else
                    {
                        return null; // Akhir antrean shuffle
                    }
                }

                _currentOriginalIndex = _shuffledIndices[nextShufflePos];
            }

            return CurrentTrack;
        }
    }

    /// <summary>
    /// Mundur ke lagu sebelumnya berdasarkan riwayat pemutaran (History Stack).
    /// </summary>
    public Track? StepPrevious()
    {
        lock (_lock)
        {
            if (_history.Count > 0)
            {
                _currentOriginalIndex = _history.Pop();
                return CurrentTrack;
            }

            if (_currentOriginalIndex > 0)
            {
                _currentOriginalIndex--;
                return CurrentTrack;
            }

            if (_repeatMode == PlaybackRepeatMode.RepeatAll && _originalList.Count > 0)
            {
                _currentOriginalIndex = _originalList.Count - 1;
                return CurrentTrack;
            }

            return CurrentTrack;
        }
    }

    /// <summary>
    /// Mengambil seluruh ID lagu dalam antrean untuk disimpan ke database.
    /// </summary>
    public List<long> GetTrackIds()
    {
        lock (_lock)
        {
            return _originalList.Select(t => t.Id).ToList();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _originalList.Clear();
            _shuffledIndices.Clear();
            _history.Clear();
            _currentOriginalIndex = -1;
        }
    }

    /// <summary>
    /// Mengacak indeks antrean menggunakan Algoritma Fisher-Yates O(N) tanpa bias.
    /// </summary>
    private void RegenerateShuffleIndices(int keepFirstIndex = -1)
    {
        _shuffledIndices.Clear();

        for (int i = 0; i < _originalList.Count; i++)
        {
            if (i != keepFirstIndex)
            {
                _shuffledIndices.Add(i);
            }
        }

        // Fisher-Yates Shuffle
        var rng = Random.Shared;
        for (int i = _shuffledIndices.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (_shuffledIndices[i], _shuffledIndices[j]) = (_shuffledIndices[j], _shuffledIndices[i]);
        }

        // Pertahankan lagu yang sedang diputar tetap di posisi teratas antrean
        if (keepFirstIndex >= 0 && keepFirstIndex < _originalList.Count)
        {
            _shuffledIndices.Insert(0, keepFirstIndex);
        }
    }
}
