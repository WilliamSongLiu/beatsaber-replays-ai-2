﻿using System.Text.Json;
using BeatLeader_Server.Models;
using static BeatLeader_Server.Utils.ResponseUtils;
using ReplayDecoder;
using System.Net.Http.Headers;
using NumSharp;
using BeatLeader_Server.Utils;
using System.Text.RegularExpressions;

public class Difficulty {
    public float Njs { get; set; }
}

public class LeaderboardInfo {
    public string Id { get; set; }
    public SongInfo Song { get; set; }
    public Difficulty? Difficulty { get; set; }
}

public class SongInfo {
    public string Hash { get; set; }
}

public class Program
{
    private static readonly HttpClient httpClient = new HttpClient();
    private static JsonSerializerOptions jsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    public static string apiUrl = "https://api.beatleader.xyz";

    public static double clamp(double num, double min, double max) {
        return Math.Min(Math.Max(num, min), max);
    }

    public static string sanitizeString(string s) {
        return Regex.Replace(s, "[^a-zA-Z0-9 ]", "");
    }

    public static void SaveNotes(List<NoteEvent> notes, string filename) {
        List<NoteEvent> notBombNotes = notes.FindAll(note => note.eventType != NoteEventType.bomb);

        if (notBombNotes.Count == 0) return;

        NDArray output = np.zeros(new Shape(notBombNotes.Count, 3));
        int index = 0;
        foreach (var note in notBombNotes)
        {
            output[index][0] = note.noteID;
            output[index][1] = note.eventType == NoteEventType.good ? 1 - clamp(note.noteCutInfo.cutDistanceToCenter / 0.3, 0, 1) : 0.0;
            output[index][2] = note.spawnTime;
            index++;
        }

        np.save(filename, output);
    }

    public static async Task DownloadReplay(string lbFolder, ScoreResponse score) {
        if (score.Offsets == null || score.Replay?.Length < 1) return;

        string filePath = Path.Combine(lbFolder, $"{score.Player.Id}-{sanitizeString(score.Player.Name)}.npy");

        if (File.Exists(filePath)) {
            return;
        }

        try {
            var request = new HttpRequestMessage(HttpMethod.Get, score.Replay);
            request.Headers.Range = new RangeHeaderValue(score.Offsets.Notes, score.Offsets.Walls);

            HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            AsyncReplayDecoder decoder = new AsyncReplayDecoder();

            using (var stream1 = new MemoryStream()) {
                await (await response.Content.ReadAsStreamAsync()).CopyToAsync(stream1);
                stream1.Position = 0;
                var notes = await decoder.DecodeNotes(stream1);
                if (notes.Count == 0) {
                    request = new HttpRequestMessage(HttpMethod.Get, score.Replay);
                    response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                    using (var stream = new MemoryStream()) {
                        await (await response.Content.ReadAsStreamAsync()).CopyToAsync(stream);
                        stream.Position = 0;
                        (var replay, var offsets) = ReplayDecoder.ReplayDecoder.Decode(stream.ToArray());

                        notes = replay?.notes ?? notes;
                    }
                }

                SaveNotes(notes, filePath);
            }
        } catch (Exception e) {
            Console.WriteLine($"Message :{e.Message}");
        }
    }

    public static async Task DownloadLeaderboardScores(string id) {
        HttpResponseMessage response = await httpClient.GetAsync($"{apiUrl}/leaderboard/scores/{id}?leaderboardContext=nomods&page=1&sortBy=rank&order=desc&count=10");
        response.EnsureSuccessStatusCode();
        string responseBody = await response.Content.ReadAsStringAsync();

        var leaderboard = JsonSerializer.Deserialize<LeaderboardResponse>(responseBody, jsonOptions);

        string lbFolder = Path.Combine("..", "replays", $"{id}-{leaderboard.Difficulty.Njs}-{sanitizeString(leaderboard.Song.Name)}");
        Directory.CreateDirectory(lbFolder);
        await Task.WhenAll(leaderboard.Scores.OrderByDescending(s => s.BaseScore).Select(s => DownloadReplay(lbFolder, s)).ToArray());
    }

    public static async Task Main(string[] args)
    {
        int leaderboardCount = 0;
        int pageSize = 100;
        int page = 1;

        do {
            try
            {
                HttpResponseMessage response = await httpClient.GetAsync(apiUrl + $"/leaderboards?leaderboardContext=nomods&page={page}&count={pageSize}&type=ranked&sortBy=playcount&order=desc&count=12&allTypes=0&allRequirements=0");
                response.EnsureSuccessStatusCode();
                string responseBody = await response.Content.ReadAsStringAsync();

                var leaderboardsPage = JsonSerializer.Deserialize<ResponseWithMetadata<LeaderboardInfo>>(responseBody, jsonOptions);

                foreach (var leaderboard in leaderboardsPage.Data)
                {
                    await DownloadLeaderboardScores(leaderboard.Id);
                    Console.WriteLine($"Leaderboard #{leaderboardCount + 1} of {leaderboardsPage.Metadata.Total}");
                    leaderboardCount++;
                }
                if (leaderboardsPage.Data.Count() < pageSize) {
                    break;
                }
                page++;

            }
            catch (HttpRequestException e)
            {
                Console.WriteLine($"\nException Caught!");
                Console.WriteLine($"Message :{e.Message}");
            }
        } while (true);
    }
}
