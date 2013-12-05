﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using BugSense;
using SDownload.Dialogs;
using TagLib;
using File = System.IO.File;
using SFile = TagLib.File;
using HtmlDocument = HtmlAgilityPack.HtmlDocument;
using Resources = SDownload.Properties.Resources;

namespace SDownload.Framework.Streams
{
    /// <summary>
    /// Represents a Song file downloaded from SoundCloud
    /// </summary>
    public class SCStream : BaseStream
    {
        /// <summary>
        /// The API client ID for SDownload
        /// </summary>
        private const String Clientid = "4515286ec9d4ace678140c3f84357b35";

        /// <summary>
        /// The JSON response containing all of the track's data from the API
        /// </summary>
        private readonly TrackData _trackData;

        /// <summary>
        /// The title of the song
        /// </summary>
        private readonly String _title;

        /// <summary>
        /// The author of the song
        /// </summary>
        private readonly String _author;

        /// <summary>
        /// The genre of the song
        /// </summary>
        public String Genre;

        /// <summary>
        /// The URL to the single song page on Soundcloud
        /// </summary>
        private readonly String _origUrl;

        /// <summary>
        /// Gather and prepare all the necessary information for downloading the actual remote resource
        /// </summary>
        /// <param name="url">The URL to the individual song</param>
        /// <param name="view">The connection associated with the browser extension</param>
        /// <returns>A Sound representation of the remote resource</returns>
        public SCStream(String url, InfoReportProxy view) : base(url, view)
        {
            _origUrl = url;
            View = view;

            try
            {
                const String resolveUrl = "http://api.soundcloud.com/resolve?url={0}&client_id={1}";
                var request = (HttpWebRequest)WebRequest.Create(String.Format(resolveUrl, url, Clientid));
                request.Method = WebRequestMethods.Http.Get;
                request.Accept = "application/json";
                var response = request.GetResponse().GetResponseStream();
                if (response == null)
                    throw new HandledException("Soundcloud API failed to respond! This could due to an issue with your connection.");
                _trackData = new DataContractJsonSerializer(typeof(TrackData)).ReadObject(response) as TrackData;
            }
            catch (Exception e)
            {
                HandledException.Throw("There was an issue connecting to the Soundcloud API.", e);
            }

            if (_trackData == null)
                throw new HandledException("Downloaded track information was corrupted!", true);

            var tokens = WebUtility.HtmlDecode(_trackData.Title).Split('-');
            _author = _trackData.User.Username;
            _title = _trackData.Title;
            Genre = _trackData.Genre ?? "";

            // Split the song name if it contains the Author
            if (tokens.Length > 1)
            {
                BugSenseHandler.Instance.LeaveBreadCrumb("Song name split");
                _author = tokens[0].Trim();
                _title = tokens[1].Trim();
            }

            var rand = GenerateRandomString(8) + ".mp3";

            var directory = Settings.DownloadFolder + GetCleanFileName(Settings.AuthorFolder ? _author : "");

            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            // Use the download url if it exists, probably better quality
            var absoluteUrl = directory + "\\" + GetCleanFileName(_title) + ".mp3";
            var artworkUrl = _trackData.ArtworkUrl ?? _trackData.User.AvatarUrl;

            Extras = new List<DownloadItem> { new DownloadItem(new Uri(artworkUrl), Path.GetTempPath() + rand + ".jpg") };


            MainResource = new DownloadItem(new Uri(GetDownloadUrl()), absoluteUrl);
        }

        /// <summary>
        /// Downloads the necessary files and handles any exceptions
        /// </summary>
        /// <param name="ignoreExtras">If the extra files associated with the main resource should be skipped</param>
        /// <returns>A task representation for keeping track of the method's progress</returns>
        public override async Task<bool> Download(bool ignoreExtras = false)
        {
            try
            {
                return await base.Download(ignoreExtras);
            }
            catch (Exception e)
            {
                if (e.Message.Contains("Not Found"))
                {
                    View.Report("Download impossible!", true);
                }
                else
                    HandledException.Throw("There was an issue downloading the necessary file(s)!", e);
                return false;
            }
        }

        /// <summary>
        /// Confirms the file tags can actually be read, proving the file is valid.
        /// </summary>
        /// <returns>True if the file was downloaded correctly and can be modified</returns>
        public override bool Validate()
        {
            if (!base.Validate())
                return false;

            // TODO: Possibly find a better way to validate more file types quicker
            // perhaps by reading the resolved url ending rather than assuming mp3 immediately
            SFile file = null;
            try
            {
                // Test if the file is a valid mp3
                file = SFile.Create(MainResource.AbsolutePath);
            }
            catch (CorruptFileException)
            {
                try
                {
                    // Check if the file is wma
                    var old = MainResource.AbsolutePath;
                    MainResource.AbsolutePath = MainResource.AbsolutePath.Substring(0,
                                                                                    MainResource.AbsolutePath.Length - 3) + "wma";
                    File.Move(old, MainResource.AbsolutePath);
                    file = SFile.Create(MainResource.AbsolutePath);
                }
                catch (CorruptFileException)
                {
                    File.Delete(MainResource.AbsolutePath);
                    // File could not be validated, Use the stream download
                    View.Report("Retrying");
                    MainResource.Uri = new Uri(GetDownloadUrl(true));
                    Download(true);
                }
            }

            return file != null;
        }

        /// <summary>
        /// Updates the ID3 tags for the song file, then moves it into iTunes if the setting is enabled.
        /// </summary>
        public override void Finish()
        {
            View.Report("Finalizing");
            try
            {
                UpdateId3Tags();
                AddToiTunes();
            }
            catch (Exception e)
            {
                // Should have been handled already
                View.Report("Invalid file!", true);
                HandledException.Throw("Invalid file was downloaded!", e);
                return;
            }

            base.Finish();
        }
        
        /// <summary>
        /// Add the song to iTunes
        /// </summary>
        private void AddToiTunes()
        {
            var newdir = String.Format("{0}\\iTunes\\iTunes Media\\Automatically Add to iTunes\\{1}.{2}", 
                Settings.CustomITunesLocation ?? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), GetCleanFileName(_title), 
                MainResource.AbsolutePath.Substring(MainResource.AbsolutePath.Length-3));

            // TODO: Ask the user first
            if (File.Exists(newdir)) 
                File.Delete(newdir);

            try
            {
                switch (Settings.TunesTransfer)
                {
                    case Settings.TunesSetting.Move:
                        {
                            BugSenseHandler.Instance.LeaveBreadCrumb("Moving song to iTunes");
                            File.Move(MainResource.AbsolutePath, newdir);

                            // Delete the artist folder if empty
                            if (Settings.AuthorFolder && MainResource.AbsolutePath.StartsWith(Settings.DownloadFolder + _author) 
                                && !Directory.EnumerateFileSystemEntries(Settings.DownloadFolder + _author).Any())
                            {
                                Directory.Delete(Settings.DownloadFolder + _author);
                            }
                            break;
                        }
                    case Settings.TunesSetting.Copy:
                        BugSenseHandler.Instance.LeaveBreadCrumb("Copying song to iTunes");
                        File.Copy(MainResource.AbsolutePath, newdir);
                        break;
                }
            }
            catch (DirectoryNotFoundException)
            {
                // Find iTunes location if it exists
                var dialog = new YesNoDialog(Resources.ErrorCantFindITunes, "Locate", "Disable")
                                 {
                                     ResponseCallback = result =>
                                                            {
                                                                if (result)
                                                                {
                                                                    // User would like to find installation on disk
                                                                    var folderBrowser = new FolderBrowserDialog
                                                                                            {
                                                                                                Description = Resources.FolderBrowserDescriptionFindITunes
                                                                                            };
                                                                    folderBrowser.ShowDialog();
                                                                    // TODO: Better validate if this is a correct iTunes directory
                                                                    if (folderBrowser.SelectedPath.EndsWith("iTunes"))
                                                                    {
                                                                        // Valid iTunes installation
                                                                        Settings.CustomITunesLocation =
                                                                            folderBrowser.SelectedPath;
                                                                    }
                                                                } else
                                                                {
                                                                    // User wants to disable iTunes functionality
                                                                    Settings.TunesTransfer =
                                                                        Settings.TunesSetting.Nothing;
                                                                }
                                                            }
                                 };
                dialog.Show();
            }
        }

        /// <summary>
        /// Update the ID3 tags
        /// </summary>
        private void UpdateId3Tags()
        {
            // Load the song file
            var song = SFile.Create(Extras[0].AbsolutePath);
            if (song == null)
                return;

            // Title
            if (!String.IsNullOrEmpty(_title))
                song.Tag.Title = _title;

            // Artist (Performer and Album Artist)
            if (!String.IsNullOrEmpty(_author))
            {
                var authorTag = new[] {_author};
                song.Tag.Performers = authorTag;
                song.Tag.AlbumArtists = authorTag;
            }

            // Genre
            if (!String.IsNullOrEmpty(Genre))
                song.Tag.Genres = new[] {Genre};

            // Album Art
            song.Tag.Pictures = new IPicture[] {new Picture(Extras[0].AbsolutePath)};

            song.Save();
        }

        /// <summary>
        /// Gets the download url for the main resource
        /// </summary>
        /// <param name="forceStream">If the provided download URL should be ignored (even if the setting enables it)</param>
        /// <param name="forceManual">If the download URL & API-provided stream url should be ignored. Parse for the media stream manually.</param>
        /// <returns>The URL the main resource can be downloaded at</returns>
        private String GetDownloadUrl(bool forceStream = false, bool forceManual = false)
        {
            var songDownload = (_trackData.DownloadUrl != null && Settings.UseDownloadLink && !forceStream) ? _trackData.DownloadUrl : _trackData.StreamUrl;
            if (songDownload == null || forceManual)
            {
                // There was no stream URL or download URL for the song, manually parse the resource stream link from the original URL
                BugSenseHandler.Instance.LeaveBreadCrumb("Manually downloading sound");
                HttpWebResponse response;
                try
                {
                    var request = (HttpWebRequest)WebRequest.Create(_origUrl);
                    response = (HttpWebResponse)request.GetResponse();
                }
                catch (Exception e)
                {
                    HandledException.Throw("Song does not allow streaming and there was an issue manually downloading the song file!", e, false);
                    return null;
                }

                var doc = new HtmlDocument();
                doc.Load(response.GetResponseStream());
                var searchString = WebUtility.HtmlDecode(doc.DocumentNode.InnerHtml);
                var links = Regex.Matches(searchString, "((http:[/][/])(media.soundcloud.com/stream/)([a-z]|[A-Z]|[0-9]|[/.]|[~]|[?]|[_]|[=])*)");
                songDownload = links[0].Value;
            }

            return songDownload + "?client_id=" + Clientid;
        }
    }
}
