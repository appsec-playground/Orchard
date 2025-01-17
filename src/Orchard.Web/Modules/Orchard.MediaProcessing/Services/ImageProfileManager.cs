﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Web;
using Orchard.ContentManagement;
using Orchard.FileSystems.Media;
using Orchard.Forms.Services;
using Orchard.Logging;
using Orchard.MediaLibrary.Models;
using Orchard.MediaProcessing.Descriptors.Filter;
using Orchard.MediaProcessing.Media;
using Orchard.MediaProcessing.Models;
using Orchard.Tokens;
using Orchard.Utility.Extensions;

namespace Orchard.MediaProcessing.Services {
    public class ImageProfileManager : IImageProfileManager {
        private readonly IStorageProvider _storageProvider;
        private readonly IImageProcessingFileNameProvider _fileNameProvider;
        private readonly IImageProfileService _profileService;
        private readonly IImageProcessingManager _processingManager;
        private readonly IOrchardServices _services;
        private readonly ITokenizer _tokenizer;

        public ImageProfileManager(
            IStorageProvider storageProvider,
            IImageProcessingFileNameProvider fileNameProvider,
            IImageProfileService profileService,
            IImageProcessingManager processingManager,
            IOrchardServices services,
            ITokenizer tokenizer) {
            _storageProvider = storageProvider;
            _fileNameProvider = fileNameProvider;
            _profileService = profileService;
            _processingManager = processingManager;
            _services = services;
            _tokenizer = tokenizer;

            Logger = NullLogger.Instance;
        }

        public ILogger Logger { get; set; }

        public string GetImageProfileUrl(string path, string profileName) {
            return GetImageProfileUrl(path, profileName, null, new FilterRecord[] { });
        }

        public string GetImageProfileUrl(string path, string profileName, ContentItem contentItem) {
            return GetImageProfileUrl(path, profileName, null, contentItem);
        }

        public string GetImageProfileUrl(string path, string profileName, FilterRecord customFilter) {
            return GetImageProfileUrl(path, profileName, customFilter, null);
        }

        public string GetImageProfileUrl(string path, string profileName, FilterRecord customFilter, ContentItem contentItem) {
            var customFilters = customFilter != null ? new FilterRecord[] { customFilter } : null;
            return GetImageProfileUrl(path, profileName, contentItem, customFilters);
        }

        public string GetImageProfileUrl(string path, string profileName, ContentItem contentItem, params FilterRecord[] customFilters) {

            // path is the publicUrl of the media, so it might contain url-encoded chars

            // try to load the processed filename from cache
            var filePath = _fileNameProvider.GetFileName(profileName, HttpUtility.UrlDecode(path));
            bool process = false;

            // Before checking everything else, ensure that the content item that needs to be processed has a ImagePart.
            // If it's not the case (e.g. if media is a svg file), processing would throw a exception.
            // If content item is null (it means it's not passed as a parameter of the ResizeMediaUrl call),
            // this function processes the file like it did before this patch;
            // this means it could possibly throw and log exceptions for svg files.
            bool checkForProfile = (contentItem == null || contentItem.Has<ImagePart>());

            if (checkForProfile) {
                //after reboot the app cache is empty so we reload the image in the cache if it exists in the _Profiles folder
                if (string.IsNullOrEmpty(filePath)) {
                    var profileFilePath = _storageProvider.Combine("_Profiles", FormatProfilePath(profileName, HttpUtility.UrlDecode(path)));

                    if (_storageProvider.FileExists(profileFilePath)) {
                        _fileNameProvider.UpdateFileName(profileName, HttpUtility.UrlDecode(path), profileFilePath);
                        filePath = profileFilePath;
                    }
                }

                // if the filename is not cached, process it
                if (string.IsNullOrEmpty(filePath)) {
                    Logger.Debug("FilePath is null, processing required, profile {0} for image {1}", profileName, path);

                    process = true;
                }
                // the processd file doesn't exist anymore, process it
                else if (!_storageProvider.FileExists(filePath)) {
                    Logger.Debug("Processed file no longer exists, processing required, profile {0} for image {1}", profileName, path);

                    process = true;
                }
                // if the original file is more recent, process it
                else if (TryGetImageLastUpdated(path, out DateTime pathLastUpdated)) {
                    var filePathLastUpdated = _storageProvider.GetFile(filePath).GetLastUpdated();

                    if (pathLastUpdated > filePathLastUpdated) {
                        Logger.Debug("Original file more recent, processing required, profile {0} for image {1}", profileName, path);

                        process = true;
                    }
                }
            }
            else {
                // Since media with no ImagePart have no profile, filePath is null, so it's set again to its original path on the storage provider.
                if (string.IsNullOrWhiteSpace(filePath)) {
                    filePath = _storageProvider.GetStoragePath(path);
                }
            }

            // todo: regenerate the file if the profile is newer, by deleting the associated filename cache entries.
            if (process) {
                Logger.Debug("Processing profile {0} for image {1}", profileName, path);

                ImageProfilePart profilePart;

                if (customFilters == null || !customFilters.Any(c => c != null)) {
                    profilePart = _profileService.GetImageProfileByName(profileName);
                    if (profilePart == null) {
                        return string.Empty;
                    }
                }
                else {
                    profilePart = _services.ContentManager.New<ImageProfilePart>("ImageProfile");
                    profilePart.Name = profileName;
                    foreach (var customFilter in customFilters) {
                        profilePart.Filters.Add(customFilter);
                    }
                }

                // prevent two requests from processing the same file at the same time
                // this is only thread safe at the machine level, so there is a try/catch later
                // to handle cross machines concurrency
                lock (string.Intern(path)) {
                    using (var image = GetImage(path)) {
                        if (image == null) {
                            return null;
                        }

                        var filterContext = new FilterContext { Media = image, FilePath = _storageProvider.Combine("_Profiles", FormatProfilePath(profileName, HttpUtility.UrlDecode(path))) };

                        var tokens = new Dictionary<string, object>();
                        // if a content item is provided, use it while tokenizing
                        if (contentItem != null) {
                            tokens.Add("Content", contentItem);
                        }

                        foreach (var filter in profilePart.Filters.OrderBy(f => f.Position)) {
                            var descriptor = _processingManager.DescribeFilters().SelectMany(x => x.Descriptors).FirstOrDefault(x => x.Category == filter.Category && x.Type == filter.Type);
                            if (descriptor == null)
                                continue;

                            var tokenized = _tokenizer.Replace(filter.State, tokens);
                            filterContext.State = FormParametersHelper.ToDynamic(tokenized);
                            descriptor.Filter(filterContext);
                        }

                        _fileNameProvider.UpdateFileName(profileName, HttpUtility.UrlDecode(path), filterContext.FilePath);

                        if (!filterContext.Saved) {
                            try {
                                var newFile = _storageProvider.OpenOrCreate(filterContext.FilePath);
                                using (var imageStream = newFile.OpenWrite()) {
                                    using (var sw = new BinaryWriter(imageStream)) {
                                        if (filterContext.Media.CanSeek) {
                                            filterContext.Media.Seek(0, SeekOrigin.Begin);
                                        }
                                        using (var sr = new BinaryReader(filterContext.Media)) {
                                            int count;
                                            var buffer = new byte[8192];
                                            while ((count = sr.Read(buffer, 0, buffer.Length)) != 0) {
                                                sw.Write(buffer, 0, count);
                                            }
                                        }
                                    }
                                    // the storage provider may have altered the filepath
                                    filterContext.FilePath = newFile.GetPath();
                                }
                            }
                            catch (Exception e) {
                                Logger.Error(e, "A profile could not be processed: " + path);
                            }
                        }

                        filterContext.Media.Dispose();
                        filePath = filterContext.FilePath;
                    }
                }
            }

            // generate a timestamped url to force client caches to update if the file has changed
            var publicUrl = _storageProvider.GetPublicUrl(filePath);
            var timestamp = _storageProvider.GetFile(filePath).GetLastUpdated().Ticks;
            return publicUrl + "?v=" + timestamp.ToString(CultureInfo.InvariantCulture);
        }

        // TODO: Update this method once the storage provider has been updated
        private Stream GetImage(string path) {
            if (path == null) {
                throw new ArgumentNullException("path");
            }

            var storagePath = _storageProvider.GetStoragePath(path);
            if (storagePath != null) {
                try {
                    var file = _storageProvider.GetFile(storagePath);
                    return file.OpenRead();
                }
                catch (Exception e) {
                    Logger.Error(e, "path:" + path + " storagePath:" + storagePath);
                }
            }

            // http://blob.storage-provider.net/my-image.jpg
            if (Uri.TryCreate(path, UriKind.Absolute, out Uri absoluteUri)) {
                return new WebClient().OpenRead(absoluteUri);
            }

            // ~/Media/Default/images/my-image.jpg
            if (VirtualPathUtility.IsAppRelative(path)) {
                var request = _services.WorkContext.HttpContext.Request;
                return new WebClient().OpenRead(new Uri(request.Url, VirtualPathUtility.ToAbsolute(path)));
            }

            return null;
        }

        private bool TryGetImageLastUpdated(string path, out DateTime lastUpdated) {
            var storagePath = _storageProvider.GetStoragePath(path);
            if (storagePath != null) {
                var file = _storageProvider.GetFile(storagePath);
                lastUpdated = file.GetLastUpdated();
                return true;
            }

            lastUpdated = DateTime.MinValue;
            return false;
        }

        private string FormatProfilePath(string profileName, string path) {
            var filenameWithExtension = Path.GetFileName(path) ?? "";
            var fileLocation = path.Substring(0, path.Length - filenameWithExtension.Length);

            return _storageProvider.Combine(
                _storageProvider.Combine(_profileService.GetNameHashCode(profileName), _profileService.GetNameHashCode(fileLocation)),
                filenameWithExtension);
        }
    }
}
