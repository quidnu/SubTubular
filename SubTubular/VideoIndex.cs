using System.Runtime.CompilerServices;
using Lifti;
using Lifti.Serialization.Binary;
using SubTubular.Extensions;

namespace SubTubular;

public sealed class VideoIndexRepository
{
    public const string FileExtension = ".idx";

    private readonly string directory;
    private readonly BinarySerializer<string> serializer;

    public VideoIndexRepository(string directory)
    {
        this.directory = directory;
        if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
        serializer = new BinarySerializer<string>();
    }

    private static FullTextIndexBuilder<string> CreateIndexBuilder()
        //see https://mikegoatly.github.io/lifti/docs/getting-started/indexing-objects/
        => new FullTextIndexBuilder<string>()
            .WithDefaultTokenization(o => o.AccentInsensitive().CaseInsensitive() // allows for easier matches
                /* only split on custom punctuation chars to enable searching for placeholders and compound words
                 * like "ever-lasting" or "can't" or "[Laughter]" or "[ __ ]", see
                 * https://github.com/mikegoatly/lifti/discussions/125
                 * https://mikegoatly.github.io/lifti/docs/index-construction/withdefaulttokenization/#splitonpunctuationbool
                 * https://research.google/blog/adding-sound-effect-information-to-youtube-captions/
                 * https://support.google.com/youtube/answer/6373554?hl=en#zippy=%2Cpotentially-inappropriate-words-in-automatic-captions */
                .SplitOnPunctuation(false).SplitOnCharacters(',', '.', '?', '!', '"'))
            // see https://mikegoatly.github.io/lifti/docs/index-construction/withobjecttokenization/
            .WithObjectTokenization<Video>(itemOptions => itemOptions
                .WithKey(v => v.Id)
                .WithField(nameof(Video.Title), v => v.Title)
                .WithField(nameof(Video.Keywords), v => v.Keywords)
                .WithField(nameof(Video.Description), v => v.Description)
                .WithDynamicFields(nameof(Video.CaptionTracks), v => v.CaptionTracks,
                    ct => ct.LanguageName, ct => ct.GetFullText() ?? string.Empty))
            .WithQueryParser(o => o.WithFuzzySearchDefaults(
                maxEditDistance: termLength => (ushort)(termLength / 3),
                // avoid returning zero here to allow for edits in the first place
                maxSequentialEdits: termLength => (ushort)(termLength < 6 ? 1 : termLength / 6)));

    internal VideoIndex Build(string key)
    {
        VideoIndex? videoIndex = null;

        // see https://mikegoatly.github.io/lifti/docs/index-construction/withindexmodificationaction/
        FullTextIndex<string> index = CreateIndexBuilder().WithIndexModificationAction(async indexSnapshot =>
        {
            await videoIndex!.AccessToken.WaitAsync();
            try { await SaveAsync(indexSnapshot, key); }
            finally { videoIndex!.AccessToken.Release(); }
        }).Build();

        videoIndex = new VideoIndex(index);
        return videoIndex;
    }

    private string GetPath(string key) => Path.Combine(directory, key + FileExtension);

    internal async ValueTask<VideoIndex?> GetAsync(string key)
    {
        var path = GetPath(key);
        var file = new FileInfo(path);
        if (!file.Exists) return null;

        var index = Build(key);

        try
        {
            await index.AccessToken.WaitAsync();

            // see https://mikegoatly.github.io/lifti/docs/serialization/
            await using (var reader = file.OpenRead())
                await serializer.DeserializeAsync(index.Index, reader, disposeStream: false);

            return index;
        }
        catch
        {
            file.Delete(); // delete corrupted index
            return null;
        }
        finally
        {
            index.AccessToken.Release();
        }
    }

    // see https://github.com/mikegoatly/lifti/issues/32 and https://github.com/mikegoatly/lifti/issues/74
    internal async ValueTask<VideoIndex> GetIndexShardAsync(string playlistKey, int shardNumber)
    {
        string key = playlistKey + "." + shardNumber;
        var index = await GetAsync(key);
        index ??= Build(key);
        return index;
    }

    private async Task SaveAsync(IIndexSnapshot<string> indexSnapshot, string key)
    {
        // see https://mikegoatly.github.io/lifti/docs/serialization/
        await using var writer = new FileStream(GetPath(key), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        await serializer.SerializeAsync(indexSnapshot, writer, disposeStream: false);
    }

    public IEnumerable<string> Delete(string? keyPrefix = null, string? key = null, ushort? notAccessedForDays = null, bool simulate = false)
        => FileDataStore.Delete(directory, FileExtension, keyPrefix, key, notAccessedForDays, simulate);
}

internal sealed class VideoIndex : IDisposable
{
    private static readonly string[] nonDynamicVideoFieldNames = [nameof(Video.Title), nameof(Video.Description), nameof(Video.Keywords)];
    internal SemaphoreSlim AccessToken = new(1, 1);
    internal FullTextIndex<string> Index { get; }

    internal VideoIndex(FullTextIndex<string> index) => Index = index;

    internal string[] GetIndexed(IEnumerable<string> videoIds)
        => [.. videoIds.Where(Index.Metadata.Contains)];

    internal async Task AddOrUpdateAsync(Video video, CancellationToken token)
    {
        /*  Adds or replaces the video, see
            https://mikegoatly.github.io/lifti/docs/index-construction/withduplicatekeybehavior/
            https://github.com/mikegoatly/lifti/discussions/124#discussioncomment-11296041 */
        await Index.AddAsync(video, token);
        video.UnIndexed = false; // to reset the flag
    }

    internal void BeginBatchChange() => Index.BeginBatchChange();
    internal Task CommitBatchChangeAsync(CancellationToken token) => Index.CommitBatchChangeAsync(token);

    /// <summary>Searches the index according to the specified <paramref name="command"/>,
    /// recombining the matches with <see cref="Video"/>s loaded using <paramref name="getVideoAsync"/>
    /// and returns <see cref="VideoSearchResult"/>s until all are processed
    /// or the <paramref name="token"/> is invoked.</summary>
    /// <param name="command">Determines the <see cref="SearchCommand.Query"/> for the search
    /// and the <see cref="PlaylistLikeScope.OrderBy"/> and <see cref="SearchCommand.Padding"/> of the results.</param>
    /// <param name="relevantVideos"><see cref="Video.Id"/>s the search is limited to
    /// accompanied by their corresponding <see cref="Video.Uploaded"/> dates, if known.
    /// The latter are only used for <see cref="SearchCommand.OrderOptions.uploaded"/>
    /// and missing dates are determined by loading the videos using <paramref name="getVideoAsync"/>.</param>
    /// <param name="playlist">Allows updating the <see cref="Playlist.GetVideos()"/>
    /// with the <see cref="Video.Uploaded"/> dates after loading them for
    /// <see cref="SearchCommand.OrderOptions.uploaded"/>.</param>
    internal async IAsyncEnumerable<VideoSearchResult> SearchAsync(SearchCommand command,
        Func<string, CancellationToken, Task<Video>> getVideoAsync,
        IDictionary<string, DateTime?>? relevantVideos = default,
        Playlist? playlist = default,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ISearchResults<string> unfiltered;

        try { unfiltered = Index.Search(command.Query!); }
        catch (LiftiException ex) when (ex.Message.StartsWith("Unknown field"))
        {
            // rethrow to attach info about available fields
            throw new InputException(ex.Message + ". Available are " + Index.FieldLookup.AllFieldNames.Join(", "), ex);
        }

        var matches = unfiltered
            // make sure to only return results for the requested videos if specified; playlist or channel indexes may contain more
            .Where(m => relevantVideos?.ContainsKey(m.Key) != false).ToList();

        Video[]? videosWithoutUploadDate = null;
        List<Video> unIndexedVideos = [];

        // order matches
        if (matches.Count > 1)
        {
            var orderByUploaded = command.OrderBy.Contains(SearchCommand.OrderOptions.uploaded);

            if (orderByUploaded)
            {
                if (relevantVideos == default) relevantVideos = new Dictionary<string, DateTime?>();

                var matchesForVideosWithoutUploadDate = matches.Where(m =>
                    !relevantVideos.ContainsKey(m.Key) || relevantVideos[m.Key] == null).ToArray();

                // get upload dates for videos that we don't know it of (may occur if index remembers a video the Playlist forgot about)
                if (matchesForVideosWithoutUploadDate.Length != 0)
                {
                    token.ThrowIfCancellationRequested();
                    var getVideos = matchesForVideosWithoutUploadDate.Select(m => getVideoAsync(m.Key, token)).ToArray();
                    await Task.WhenAll(getVideos).WithAggregateException();
                    videosWithoutUploadDate = [.. getVideos.Select(t => t.Result)];
                    unIndexedVideos.AddRange(videosWithoutUploadDate.Where(v => v.UnIndexed));

                    foreach (var match in matchesForVideosWithoutUploadDate)
                    {
                        token.ThrowIfCancellationRequested();
                        Video video = videosWithoutUploadDate.Single(v => v.Id == match.Key);
                        relevantVideos[match.Key] = video.Uploaded;
                        playlist?.Update(video);
                    }
                }
            }

            if (command.OrderBy.ContainsAny(SearchCommand.Orders))
            {
                var orderded = command.OrderBy.Contains(SearchCommand.OrderOptions.asc)
                    ? matches.OrderBy(m => orderByUploaded ? relevantVideos![m.Key] : m.Score as object)
                    : matches.OrderByDescending(m => orderByUploaded ? relevantVideos![m.Key] : m.Score as object);

                matches = [.. orderded];
            }
        }

        foreach (var match in matches)
        {
            token.ThrowIfCancellationRequested();

            // consider results for un-cached videos stale
            if (unIndexedVideos.Any(video => video.Id == match.Key)) continue;

            var video = videosWithoutUploadDate?.SingleOrDefault(v => v.Id == match.Key);

            if (video == null)
            {
                video = await getVideoAsync(match.Key, token);

                if (video.UnIndexed)
                {
                    unIndexedVideos.Add(video);
                    continue; // consider results for un-cached videos stale
                }
            }

            var result = new VideoSearchResult { Video = video, Score = match.Score };

            var titleMatches = match.FieldMatches.Where(m => m.FoundIn == nameof(Video.Title));

            if (titleMatches.Any()) result.TitleMatches = new MatchedText(video.Title,
                [.. titleMatches.SelectMany(m => m.Locations).Select(m => new MatchedText.Match(m.Start, m.Length))]);

            var descriptionMatches = match.FieldMatches.Where(m => m.FoundIn == nameof(Video.Description));

            if (descriptionMatches.Any()) result.DescriptionMatches = new MatchedText(video.Description,
                [.. descriptionMatches.SelectMany(m => m.Locations).Select(l => new MatchedText.Match(l.Start, l.Length))]);

            var keywordMatches = match.FieldMatches.Where(m => m.FoundIn == nameof(Video.Keywords));

            if (keywordMatches.Any())
            {
                var joinedKeywords = string.Empty;

                // remembers the index in the list of keywords and start index in joinedKeywords for each keyword
                var keywordInfos = video.Keywords.Select((keyword, index) =>
                {
                    var info = new { index, Start = joinedKeywords.Length };
                    joinedKeywords += keyword;
                    return info;
                }).ToArray();

                result.KeywordMatches = [.. keywordMatches.SelectMany(match => match.Locations)
                    .Select(location => new
                    {
                        location, // represents the match location in joinedKeywords
                                  // used to calculate the match index within a matched keyword
                        keywordInfo = keywordInfos.TakeWhile(info => info.Start <= location.Start).Last()
                    })
                    .GroupBy(x => x.keywordInfo.index) // group matches by keyword
                    .OrderBy(g => g.Key)
                    .Select(g => new MatchedText(video.Keywords[g.Key],
                        [.. g.Select(x => new MatchedText.Match(
                            // recalculate match index relative to keyword start
                            start: x.location.Start - x.keywordInfo.Start,
                            length: x.location.Length))]))];
            }

            var captionTrackMatches = match.FieldMatches.Where(m => !nonDynamicVideoFieldNames.Contains(m.FoundIn));

            if (captionTrackMatches.Any()) result.MatchingCaptionTracks = [.. captionTrackMatches.Select(m =>
            {
                var track = video.CaptionTracks?.SingleOrDefault(t => t.LanguageName == m.FoundIn);
                if (track == null) return null;

                MatchedText matches = new(track.GetFullText()!,
                    [.. m.Locations.Select(match => new MatchedText.Match(match.Start, match.Length))]);

                return new VideoSearchResult.CaptionTrackResult { Track = track, Matches = matches };
            }).WithValue()];

            yield return result;
        }

        if (unIndexedVideos.Count > 0)
        {
            // consider results for un-cached videos stale and re-index them
            await UpdateAsync(unIndexedVideos, token);

            await foreach (var result in SearchAsync(command, GetReIndexedVideoAsync,
                unIndexedVideos.ToDictionary(v => v.Id, v => v.Uploaded as DateTime?),
                playlist, token))
                yield return result;

            // re-trigger search for re-indexed videos only
            async Task<Video> GetReIndexedVideoAsync(string id, CancellationToken token)
                => unIndexedVideos.SingleOrDefault(v => v.Id == id) ?? await getVideoAsync(id, token);
        }
    }

    private async Task UpdateAsync(IEnumerable<Video> videos, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var indexedKeys = Index.Metadata.GetIndexedDocuments().Select(d => d.Key).ToArray();
        BeginBatchChange();

        foreach (var video in videos)
        {
            token.ThrowIfCancellationRequested();

            await Task.WhenAll(indexedKeys.Where(key => key == video.Id)
                .Select(key => Index.RemoveAsync(key))).WithAggregateException();

            await AddOrUpdateAsync(video, token);
        }

        await CommitBatchChangeAsync(token);
    }

    public void Dispose()
    {
        Index.Dispose();
        AccessToken.Dispose();
    }
}

internal static class VideoIndexExtensions
{
    internal static bool SpansMultipleIndexShards(this PlaylistLikeScope scope)
    {
        var playlist = scope.SingleValidated.Playlist!;
        var videos = playlist.GetVideos().Skip(scope.Skip).Take(scope.Take);
        return videos.GroupBy(v => v.ShardNumber).Count() > 1;
    }
}