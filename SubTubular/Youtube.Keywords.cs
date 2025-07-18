using System.Runtime.CompilerServices;
using System.Threading.Channels;
using SubTubular.Extensions;

namespace SubTubular;

partial class Youtube
{
    /// <summary>Returns the <see cref="Video.Keywords"/> and their corresponding number of occurrences
    /// from the videos scoped by <paramref name="command"/>.</summary>
    public async IAsyncEnumerable<(string[] keywords, string videoId, CommandScope scope)> ListKeywordsAsync(ListKeywords command,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        var channel = Channel.CreateUnbounded<(string[] keywords, string videoId, CommandScope scope)>(
            new UnboundedChannelOptions { SingleReader = true });

        var lookupTasks = command.GetPlaylistLikeScopes().GetValid()
            .Select(scope => RunUpdatingScope(ForPlaylists(scope), scope)).ToList();

        if (command.HasValidVideos)
        {
            VideosScope videos = command.Videos!;
            lookupTasks.Add(RunUpdatingScope(ForVideos(videos), videos));
        }

        // hook up writer completion before starting to read to ensure the reader knows when it's done
        var lookups = Task.WhenAll(lookupTasks).WithAggregateException()
            .ContinueWith(t =>
            {
                channel.Writer.Complete();
                if (t.IsFaulted) throw t.Exception; // bubble up error
                // cancellation needs no handling because lookups use the same token as this task
            }, token); // reader uses the same token, writer completion is not required.

        // start reading
        await foreach (var keywords in channel.Reader.ReadAllAsync(token)) yield return keywords;

        await lookups;

        async Task RunUpdatingScope(Task listingKeywords, CommandScope scope)
        {
            try
            {
                await listingKeywords; // to throw exceptions
                scope.Report(VideoList.Status.searched);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { scope.Report(VideoList.Status.canceled); }
            catch (Exception ex)
            {
                scope.Report(VideoList.Status.failed);
                scope.Notify("Error listing keywords", errors: [ex]);
            }
        }

        async Task ForPlaylists(PlaylistLikeScope scope)
        {
            var playlist = scope.SingleValidated.Playlist!;

            await using (playlist.CreateChangeToken(() => dataStore.SetAsync(scope.StorageKey, playlist)))
            {
                Task? continuedRefresh = await RefreshPlaylistAsync(scope, token);
                var videos = playlist.GetVideos().Skip(scope.Skip).Take(scope.Take).ToArray();
                var videoIds = videos.Ids().ToArray();
                scope.QueueVideos(videoIds);
                scope.Report(VideoList.Status.searching);

                foreach (var video in videos)
                {
                    if (video.Keywords?.Length > 0)
                        await channel.Writer.WriteAsync((video.Keywords, video.Id, scope), token);

                    scope.Report(video.Id, VideoList.Status.searched);
                }

                scope.Report(VideoList.Status.searched); // early, independent of background refresh
                if (continuedRefresh != null) await continuedRefresh;
            }
        }

        async Task ForVideos(VideosScope videos)
        {
            var videoIds = videos.GetRemoteValidated().Ids().ToArray();
            videos.QueueVideos(videoIds);
            videos.Report(VideoList.Status.searching);

            await Task.WhenAll(videoIds.Select(async videoId =>
            {
                videos.Report(videoId, VideoList.Status.searching);
                var video = await GetVideoAsync(videoId, token, videos);
                await channel.Writer.WriteAsync((video.Keywords, videoId, videos), token);
                videos.Report(videoId, VideoList.Status.searched);
            })).WithAggregateException();
        }
    }

    public static void AggregateKeywords(string[] keywords, string videoId, CommandScope scope,
        Dictionary<CommandScope, Dictionary<string, List<string>>> keywordsByScope)
    {
        if (keywordsByScope.TryGetValue(scope, out Dictionary<string, List<string>>? videoIdsByKeyword))
        {
            foreach (var keyword in keywords)
            {
                if (videoIdsByKeyword.TryGetValue(keyword, out List<string>? videoIds)) videoIds.Add(videoId);
                else videoIdsByKeyword.Add(keyword, [videoId]);
            }
        }
        else keywordsByScope.Add(scope, keywords.ToDictionary(kw => kw, _ => new List<string> { videoId }));
    }

    public static Dictionary<CommandScope, (string keyword, int foundInVideos)[]> CountKeywordVideos(
        Dictionary<CommandScope, Dictionary<string, List<string>>> keywordsByScope)
        => keywordsByScope.ToDictionary(s => s.Key,
            s => s.Value.OrderByDescending(pair => pair.Value.Count).ThenBy(pair => pair.Key)
                .Select(p => (p.Key, p.Value.Count)).ToArray());
}
