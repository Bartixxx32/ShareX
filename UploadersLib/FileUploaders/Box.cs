﻿#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (C) 2008-2014 ShareX Developers

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using HelpersLib;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Xml.Linq;
using UploadersLib.HelperClasses;

namespace UploadersLib.FileUploaders
{
    public sealed class Box : FileUploader, IOAuth2
    {
        public OAuth2Info AuthInfo { get; set; }
        public string FolderID { get; set; }
        public bool Share { get; set; }

        public Box(OAuth2Info oauth)
        {
            AuthInfo = oauth;
            FolderID = "0";
            Share = true;
        }

        public string GetAuthorizationURL()
        {
            Dictionary<string, string> args = new Dictionary<string, string>();
            args.Add("response_type", "code");
            args.Add("client_id", AuthInfo.Client_ID);

            return CreateQuery("https://www.box.com/api/oauth2/authorize", args);
        }

        public bool GetAccessToken(string pin)
        {
            Dictionary<string, string> args = new Dictionary<string, string>();
            args.Add("grant_type", "authorization_code");
            args.Add("code", pin);
            args.Add("client_id", AuthInfo.Client_ID);
            args.Add("client_secret", AuthInfo.Client_Secret);

            string response = SendRequest(HttpMethod.POST, "https://www.box.com/api/oauth2/token", args);

            if (!string.IsNullOrEmpty(response))
            {
                OAuth2Token token = JsonConvert.DeserializeObject<OAuth2Token>(response);

                if (token != null && !string.IsNullOrEmpty(token.access_token))
                {
                    token.UpdateExpireDate();
                    AuthInfo.Token = token;
                    return true;
                }
            }

            return false;
        }

        public bool RefreshAccessToken()
        {
            if (OAuth2Info.CheckOAuth(AuthInfo) && !string.IsNullOrEmpty(AuthInfo.Token.refresh_token))
            {
                Dictionary<string, string> args = new Dictionary<string, string>();
                args.Add("grant_type", "refresh_token");
                args.Add("refresh_token", AuthInfo.Token.refresh_token);
                args.Add("client_id", AuthInfo.Client_ID);
                args.Add("client_secret", AuthInfo.Client_Secret);

                string response = SendRequest(HttpMethod.POST, "https://www.box.com/api/oauth2/token", args);

                if (!string.IsNullOrEmpty(response))
                {
                    OAuth2Token token = JsonConvert.DeserializeObject<OAuth2Token>(response);

                    if (token != null && !string.IsNullOrEmpty(token.access_token))
                    {
                        token.UpdateExpireDate();
                        AuthInfo.Token = token;
                        return true;
                    }
                }
            }

            return false;
        }

        public bool CheckAuthorization()
        {
            if (OAuth2Info.CheckOAuth(AuthInfo))
            {
                if (AuthInfo.Token.IsExpired && !RefreshAccessToken())
                {
                    Errors.Add("Refresh access token failed.");
                    return false;
                }
            }
            else
            {
                Errors.Add("Box login is required.");
                return false;
            }

            return true;
        }

        public BoxFolder GetAccountTree(string folderID = "0", bool onelevel = false, bool nofiles = false, bool nozip = true, bool simple = false)
        {
            NameValueCollection args = new NameValueCollection();
            args.Add("action", "get_account_tree");
            args.Add("folder_id", folderID);

            if (onelevel) // Make a tree of one level depth, so you will get only the files and folders stored in the folder of the folder_id you have provided.
            {
                args.Add("params", "onelevel");
            }

            if (nofiles) // Only include the folders in the user account tree, and ignore the files.
            {
                args.Add("params", "nofiles");
            }

            if (nozip) // Do not zip the tree xml.
            {
                args.Add("params", "nozip");
            }

            if (simple) // Display the full tree with a limited list of attributes to make for smaller, more efficient output (folders only contain the 'name' and 'id' attributes, and files will contain the 'name', 'id', 'created', and 'size' attributes)
            {
                args.Add("params", "simple");
            }

            string url = CreateQuery("", args);

            string response = SendRequest(HttpMethod.GET, url);

            if (!string.IsNullOrEmpty(response))
            {
                XDocument xd = XDocument.Parse(response);
                XElement xe = xd.GetElement("response");

                if (xe != null && xe.GetElementValue("status") == "listing_ok")
                {
                    XElement xeTree = xe.Element("tree");

                    if (xeTree != null)
                    {
                        return ParseFolder(xeTree.Element("folder"));
                    }
                }
            }

            return null;
        }

        private BoxFolder ParseFolder(XElement xe)
        {
            if (xe != null && xe.Name == "folder")
            {
                BoxFolder folder = new BoxFolder();
                folder.ID = xe.GetAttributeValue("id");
                folder.Name = xe.GetAttributeValue("name");

                XElement xeFolders = xe.Element("folders");

                if (xeFolders != null)
                {
                    foreach (XElement xeFolder in xeFolders.Elements())
                    {
                        BoxFolder childFolder = ParseFolder(xeFolder);

                        if (childFolder != null)
                        {
                            folder.Folders.Add(childFolder);
                        }
                    }
                }

                return folder;
            }

            return null;
        }

        public BoxFolder GetFolderList()
        {
            return GetAccountTree("0", false, true, true, true);
        }

        public string CreateSharedLink(string id)
        {
            NameValueCollection headers = new NameValueCollection();
            headers.Add("Authorization", "Bearer " + AuthInfo.Token.access_token);

            string response = SendRequest(HttpMethod.PUT, "https://api.box.com/2.0/files/" + id, "{\"shared_link\": {\"access\": \"open\"}}", headers: headers);

            if (!string.IsNullOrEmpty(response))
            {
                BoxFileEntry fileEntry = JsonConvert.DeserializeObject<BoxFileEntry>(response);

                if (fileEntry != null && fileEntry.shared_link != null)
                {
                    return fileEntry.shared_link.url;
                }
            }

            return null;
        }

        public override UploadResult Upload(Stream stream, string fileName)
        {
            if (!CheckAuthorization())
            {
                return null;
            }

            Dictionary<string, string> args = new Dictionary<string, string>();
            args.Add("parent_id", FolderID);

            NameValueCollection headers = new NameValueCollection();
            headers.Add("Authorization", "Bearer " + AuthInfo.Token.access_token);

            UploadResult result = UploadData(stream, "https://upload.box.com/api/2.0/files/content", fileName, "filename", args, headers: headers);

            if (result.IsSuccess)
            {
                BoxFileInfo fileInfo = JsonConvert.DeserializeObject<BoxFileInfo>(result.Response);

                if (fileInfo != null && fileInfo.entries != null && fileInfo.entries.Length > 0)
                {
                    if (Share)
                    {
                        result.URL = CreateSharedLink(fileInfo.entries[0].id);
                    }
                    else
                    {
                        result.URL = string.Format("https://app.box.com/files/0/f/{0}/1/f_{1}", "folder", "file");
                    }
                }
            }

            return result;
        }
    }

    public class BoxFileInfo
    {
        public BoxFileEntry[] entries { get; set; }
    }

    public class BoxFileEntry
    {
        public string id { get; set; }
        public string name { get; set; }
        public BoxFileSharedLink shared_link { get; set; }
    }

    public class BoxFileSharedLink
    {
        public string url { get; set; }
    }

    public class BoxFolder
    {
        public string ID;
        public string Name;
        public string User_id;
        public string Description;
        public string Shared;
        public string Shared_link;
        public string Permissions;

        //public List<BoxTag> Tags;
        //public List<BoxFile> Files;
        public List<BoxFolder> Folders = new List<BoxFolder>();
    }
}