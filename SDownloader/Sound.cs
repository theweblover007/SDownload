﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Newtonsoft.Json;
using TagLib;
using SFile = TagLib.File;
using HtmlDocument = HtmlAgilityPack.HtmlDocument;

namespace SDownload
{
    /// <summary>
    /// Represents a Song file downloaded from SoundCloud
    /// </summary>
    public class Sound
    {
        private const String clientid = "4515286ec9d4ace678140c3f84357b35";
        private bool _songDownloaded = false;
        private readonly Queue<KeyValuePair<Uri, String>> _downloads = new Queue<KeyValuePair<Uri, String>>();
        public String Title;
        public String Author;
        public String Genre;

        private readonly String _filename;

        public static NotifyHandler Notify = null;

        public Sound(String filename, String title = "", String author = "", String genre = "")
        {
            Title = title;
            Author = author;
            Genre = genre;

            _filename = filename;
        }

        /// <summary>
        /// Add the song to iTunes
        /// </summary>
        public void AddToTunes()
        {
            var old = String.Format("{0}{1}\\{2}.mp3", Settings.DownloadFolder, Settings.AuthorFolder ? Author : "", GetFileName(Title));
            var newdir = String.Format("{0}\\iTunes\\iTunes Media\\Automatically Add to iTunes\\{1}.mp3", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), GetFileName(Title));
            try
            {
                switch (Settings.TunesTransfer)
                {
                    case Settings.TunesSetting.Move:
                        {
                            System.IO.File.Move(old, newdir);

                            // Delete the artist folder if empty
                            if (Settings.AuthorFolder && old.StartsWith(Settings.DownloadFolder + Author) 
                                && !Directory.EnumerateFileSystemEntries(Settings.DownloadFolder + Author).Any())
                            {
                                Directory.Delete(Settings.DownloadFolder + Author);
                            }
                            break;
                        }
                    case Settings.TunesSetting.Copy:
                        System.IO.File.Copy(old, newdir);
                        break;
                }
            }
            catch (DirectoryNotFoundException)
            {
                // iTunes directory does not exist
                MessageBox.Show("iTunes could not be found on your computer!");
            }
        }

        /// <summary>
        /// Update the ID3 tags
        /// </summary>
        public void Update()
        {
            var song = SFile.Create(Settings.DownloadFolder + (Settings.AuthorFolder ? Author : "") + "\\" +  GetFileName(Title) + ".mp3");

            if (song == null)
                return;

            if (!String.IsNullOrEmpty(Title))
                song.Tag.Title = Title;

            if (!String.IsNullOrEmpty(Author)) 
            {
                song.Tag.Performers = new[] {Author};
            }

            if (!String.IsNullOrEmpty(Genre))
                song.Tag.Genres = new[] {Genre};

            song.Tag.Pictures = new IPicture[] {new Picture(Path.GetTempPath() + "\\" + _filename + ".jpg")};

            song.Save();
        }

        /// <summary>
        /// Download a SoundCloud song
        /// </summary>
        /// <param name="url">The url of the song</param>
        /// <returns>A Sound representation of the song</returns>
        public static Sound Download(String url)
        {
            Notify.Show("Downloading link information...");
            TrackData track;
            try
            {
                const String resolveUrl = "http://api.soundcloud.com/resolve?url={0}&client_id={1}";
                var request = (HttpWebRequest) WebRequest.Create(String.Format(resolveUrl, url, clientid));
                request.Method = WebRequestMethods.Http.Get;
                request.Accept = "application/json";
                var response = request.GetResponse().GetResponseStream();
                if (response == null)
                    throw new Exception("Soundcloud API failed to respond!");
                track = new DataContractJsonSerializer(typeof(TrackData)).ReadObject(response) as TrackData;
                if (track == null)
                    throw new Exception("Could not deserialize the track information!");
            }
            catch (Exception e)
            {
                MessageBox.Show(String.Format("Unable to make a connection to the URL: {0}\n\n{1}", url, e.ToString()));
                return null;
            }

            var tokens = WebUtility.HtmlDecode(track.Title).Split('-');
            var author = track.User.Username;
            var title = track.Title;

            if (tokens.Length > 1)
            {
                author = tokens[0].Trim();
                title = tokens[1].Trim();
            }

            var rand = RandomString(8) + ".mp3";
            var s = new Sound(rand, title, author, track.Genre ?? "");

            Notify.Show(String.Format("Downloading {0} by {1}", title, author));
            String directory = Settings.DownloadFolder + (Settings.AuthorFolder ? author : "");
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            // Use the download url if it exists, probably better quality
            String songDownload = track.DownloadUrl != null && Settings.UseDownloadLink ? track.DownloadUrl : track.StreamUrl;
            if (songDownload == null)
            {
                HttpWebResponse response;
                try
                {
                    var request = (HttpWebRequest) WebRequest.Create(url);
                    response = (HttpWebResponse) request.GetResponse();
                }
                catch (Exception e)
                {
                    MessageBox.Show(
                        String.Format(
                            "Song ({0} by {1}) does not allow streaming and there was an issue manually downloading the song file! ",
                            title, author));
                    return null;
                }
                var doc = new HtmlDocument();
                doc.Load(response.GetResponseStream());
                var searchString = WebUtility.HtmlDecode(doc.DocumentNode.InnerHtml);
                var links = Regex.Matches(searchString, "((http:[/][/])(media.soundcloud.com/stream/)([a-z]|[A-Z]|[0-9]|[/.]|[~]|[?]|[_]|[=])*)");
                songDownload = links[0].Value;
            }
            s._downloads.Enqueue(new KeyValuePair<Uri, String>(new Uri(songDownload + "?client_id=" + clientid), directory + "\\" + GetFileName(title) + ".mp3"));
            s._downloads.Enqueue(new KeyValuePair<Uri, String>(new Uri(track.ArtworkUrl ?? track.User.AvatarUrl), Path.GetTempPath() + "\\" + rand + ".jpg"));

            s.DownloadItems();

            return s;
        }

        private void DownloadItems()
        {
            if (_downloads.Count > 0)
            {
                var url = _downloads.Dequeue();

                var wc = new WebClient();
                wc.DownloadFileCompleted += WcDownloadFileCompleted;

                if (!_songDownloaded)
                    wc.DownloadProgressChanged += WcDownloadProgressChanged;

                wc.DownloadFileAsync(url.Key, url.Value);
            }
            else
            {
                Notify.UpdateText("Finalizing download!");
                PackageAndDeploy();
            }
        }

        private void WcDownloadProgressChanged(object sender, DownloadProgressChangedEventArgs e)
        {
            Notify.UpdateText(String.Format("Progress: {0}% | {1}", e.ProgressPercentage, Title));
        }

        private void WcDownloadFileCompleted(object sender, AsyncCompletedEventArgs e)
        {
            if (e.Error == null)
            {
                if (!_songDownloaded)
                {
                    _songDownloaded = true;
                    Notify.Show("Downloading song information...");
                }

                DownloadItems();
            }
            else
            {
                MessageBox.Show(e.Error.ToString());
                Application.Exit();
            }
        }

        public void PackageAndDeploy()
        {
            Update();
            AddToTunes();
            Notify.Show(String.Format("{0} download completed!", Title), true);
        }

        /// <summary>
        /// Generate a random string of a given length
        /// </summary>
        /// <param name="size">Length of the string</param>
        /// <see cref="http://stackoverflow.com/questions/1122483/c-sharp-random-string-generator"/>
        /// <returns>A randomized string </returns>
        private static String RandomString(int size)
        {
            var builder = new StringBuilder();
            var random = new Random();
            for (var i = 0; i < size; i++)
            {
                var ch = Convert.ToChar(Convert.ToInt32(Math.Floor(26 * random.NextDouble() + 65)));
                builder.Append(ch);
            }

            return builder.ToString();
        }

        /// <summary>
        /// Get the clean filename for a title
        /// </summary>
        /// <param name="value">The title of the song</param>
        /// <returns>A cleaned filename for the given title</returns>
        public static string GetFileName(string value)
        {
            StringBuilder sb = new StringBuilder(value);
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (char item in invalid)
            {
                sb.Replace(item.ToString(), "");
            }
            return sb.ToString();
        }
    }
}
