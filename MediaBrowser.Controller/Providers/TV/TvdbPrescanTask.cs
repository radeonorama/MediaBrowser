﻿using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Extensions;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Net;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace MediaBrowser.Controller.Providers.TV
{
    /// <summary>
    /// Class TvdbPrescanTask
    /// </summary>
    public class TvdbPrescanTask : ILibraryPrescanTask
    {
        /// <summary>
        /// The server time URL
        /// </summary>
        private const string ServerTimeUrl = "http://thetvdb.com/api/Updates.php?type=none";

        /// <summary>
        /// The updates URL
        /// </summary>
        private const string UpdatesUrl = "http://thetvdb.com/api/Updates.php?type=all&time={0}";

        /// <summary>
        /// The _HTTP client
        /// </summary>
        private readonly IHttpClient _httpClient;
        /// <summary>
        /// The _logger
        /// </summary>
        private readonly ILogger _logger;
        /// <summary>
        /// The _config
        /// </summary>
        private readonly IConfigurationManager _config;

        /// <summary>
        /// Initializes a new instance of the <see cref="TvdbPrescanTask"/> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        /// <param name="httpClient">The HTTP client.</param>
        /// <param name="config">The config.</param>
        public TvdbPrescanTask(ILogger logger, IHttpClient httpClient, IConfigurationManager config)
        {
            _logger = logger;
            _httpClient = httpClient;
            _config = config;
        }

        /// <summary>
        /// Runs the specified progress.
        /// </summary>
        /// <param name="progress">The progress.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task.</returns>
        public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var path = RemoteSeriesProvider.GetSeriesDataPath(_config.CommonApplicationPaths);

            var timestampFile = Path.Combine(path, "time.txt");

            var timestampFileInfo = new FileInfo(timestampFile);

            // Don't check for tvdb updates anymore frequently than 24 hours
            if (timestampFileInfo.Exists && (DateTime.UtcNow - timestampFileInfo.LastWriteTimeUtc).TotalDays < 1)
            {
                return;
            }

            // Find out the last time we queried tvdb for updates
            var lastUpdateTime = timestampFileInfo.Exists ? File.ReadAllText(timestampFile, Encoding.UTF8) : string.Empty;

            string newUpdateTime;

            var existingDirectories = Directory.EnumerateDirectories(path).Select(Path.GetFileName).ToList();

            // If this is our first time, update all series
            if (string.IsNullOrEmpty(lastUpdateTime))
            {
                // First get tvdb server time
                using (var stream = await _httpClient.Get(new HttpRequestOptions
                {
                    Url = ServerTimeUrl,
                    CancellationToken = cancellationToken,
                    EnableHttpCompression = true,
                    ResourcePool = RemoteSeriesProvider.Current.TvDbResourcePool

                }).ConfigureAwait(false))
                {
                    var doc = new XmlDocument();

                    doc.Load(stream);

                    newUpdateTime = doc.SafeGetString("//Time");
                }

                await UpdateSeries(existingDirectories, path, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var seriesToUpdate = await GetSeriesIdsToUpdate(existingDirectories, lastUpdateTime, cancellationToken).ConfigureAwait(false);

                newUpdateTime = seriesToUpdate.Item2;

                await UpdateSeries(seriesToUpdate.Item1, path, cancellationToken).ConfigureAwait(false);
            }

            File.WriteAllText(timestampFile, newUpdateTime, Encoding.UTF8);
        }

        /// <summary>
        /// Gets the series ids to update.
        /// </summary>
        /// <param name="existingSeriesIds">The existing series ids.</param>
        /// <param name="lastUpdateTime">The last update time.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task{IEnumerable{System.String}}.</returns>
        private async Task<Tuple<IEnumerable<string>, string>> GetSeriesIdsToUpdate(IEnumerable<string> existingSeriesIds, string lastUpdateTime, CancellationToken cancellationToken)
        {
            // First get last time
            using (var stream = await _httpClient.Get(new HttpRequestOptions
            {
                Url = string.Format(UpdatesUrl, lastUpdateTime),
                CancellationToken = cancellationToken,
                EnableHttpCompression = true,
                ResourcePool = RemoteSeriesProvider.Current.TvDbResourcePool

            }).ConfigureAwait(false))
            {
                var doc = new XmlDocument();

                doc.Load(stream);

                var newUpdateTime = doc.SafeGetString("//Time");

                var seriesNodes = doc.SelectNodes("//Series");

                var seriesList = seriesNodes == null ? new string[] { } :
                    seriesNodes.Cast<XmlNode>()
                    .Select(i => i.InnerText)
                    .Where(i => !string.IsNullOrWhiteSpace(i) && existingSeriesIds.Contains(i, StringComparer.OrdinalIgnoreCase));

                return new Tuple<IEnumerable<string>, string>(seriesList, newUpdateTime);
            }
        }

        /// <summary>
        /// Updates the series.
        /// </summary>
        /// <param name="seriesIds">The series ids.</param>
        /// <param name="seriesDataPath">The series data path.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task.</returns>
        private async Task UpdateSeries(IEnumerable<string> seriesIds, string seriesDataPath, CancellationToken cancellationToken)
        {
            foreach (var seriesId in seriesIds)
            {
                try
                {
                    await UpdateSeries(seriesId, seriesDataPath, cancellationToken).ConfigureAwait(false);
                }
                catch (HttpException ex)
                {
                    // Already logged at lower levels, but don't fail the whole operation, unless timed out

                    if (ex.IsTimedOut)
                    {
                        throw;
                    }
                }
            }
        }

        /// <summary>
        /// Updates the series.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <param name="seriesDataPath">The series data path.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task.</returns>
        private Task UpdateSeries(string id, string seriesDataPath, CancellationToken cancellationToken)
        {
            _logger.Info("Updating series " + id);

            seriesDataPath = Path.Combine(seriesDataPath, id);

            if (!Directory.Exists(seriesDataPath))
            {
                Directory.CreateDirectory(seriesDataPath);
            }
            
            return RemoteSeriesProvider.Current.DownloadSeriesZip(id, seriesDataPath, cancellationToken);
        }
    }
}