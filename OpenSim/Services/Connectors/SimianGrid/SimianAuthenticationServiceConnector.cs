﻿/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Specialized;
using System.Reflection;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;

namespace OpenSim.Services.Connectors.SimianGrid
{
    /// <summary>
    /// Connects authentication/authorization to the SimianGrid backend
    /// </summary>
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule")]
    public class SimianAuthenticationServiceConnector : IAuthenticationService, ISharedRegionModule
    {
        private static readonly ILog m_log =
                LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        private string m_serverUrl = String.Empty;

        #region ISharedRegionModule

        public Type ReplaceableInterface { get { return null; } }
        public void RegionLoaded(Scene scene) { }
        public void PostInitialise() { }
        public void Close() { }

        public SimianAuthenticationServiceConnector() { }
        public string Name { get { return "SimianAuthenticationServiceConnector"; } }
        public void AddRegion(Scene scene) { if (!String.IsNullOrEmpty(m_serverUrl)) { scene.RegisterModuleInterface<IAuthenticationService>(this); } }
        public void RemoveRegion(Scene scene) { if (!String.IsNullOrEmpty(m_serverUrl)) { scene.UnregisterModuleInterface<IAuthenticationService>(this); } }

        #endregion ISharedRegionModule

        public SimianAuthenticationServiceConnector(IConfigSource source)
        {
            Initialise(source);
        }

        public void Initialise(IConfigSource source)
        {
            if (Simian.IsSimianEnabled(source, "AuthenticationServices", this.Name))
            {
                IConfig assetConfig = source.Configs["AuthenticationService"];
                if (assetConfig == null)
                {
                    m_log.Error("[SIMIAN AUTH CONNECTOR]: AuthenticationService missing from OpenSim.ini");
                    throw new Exception("Authentication connector init error");
                }

                string serviceURI = assetConfig.GetString("AuthenticationServerURI");
                if (String.IsNullOrEmpty(serviceURI))
                {
                    m_log.Error("[SIMIAN AUTH CONNECTOR]: No Server URI named in section AuthenticationService");
                    throw new Exception("Authentication connector init error");
                }

                m_serverUrl = serviceURI;
            }
        }

        public string Authenticate(UUID principalID, string password, int lifetime)
        {
            NameValueCollection requestArgs = new NameValueCollection
            {
                { "RequestMethod", "GetIdentities" },
                { "UserID", principalID.ToString() }
            };

            OSDMap response = WebUtil.PostToService(m_serverUrl, requestArgs);
            if (response["Success"].AsBoolean() && response["Identities"] is OSDArray)
            {
                bool md5hashFound = false;

                OSDArray identities = (OSDArray)response["Identities"];
                for (int i = 0; i < identities.Count; i++)
                {
                    OSDMap identity = identities[i] as OSDMap;
                    if (identity != null)
                    {
                        if (identity["Type"].AsString() == "md5hash")
                        {
                            string credential = identity["Credential"].AsString();

                            if (password == credential || "$1$" + Utils.MD5String(password) == credential || Utils.MD5String(password) == credential)
                                return Authorize(principalID);

                            md5hashFound = true;
                            break;
                        }
                    }
                }

                if (md5hashFound)
                    m_log.Warn("[SIMIAN AUTH CONNECTOR]: Authentication failed for " + principalID + " using md5hash $1$" + Utils.MD5String(password));
                else
                    m_log.Warn("[SIMIAN AUTH CONNECTOR]: Authentication failed for " + principalID + ", no md5hash identity found");
            }
            else
            {
                m_log.Warn("[SIMIAN AUTH CONNECTOR]: Failed to retrieve identities for " + principalID + ": "  +
                    response["Message"].AsString());
            }

            return String.Empty;
        }

        public bool Verify(UUID principalID, string token, int lifetime)
        {
            NameValueCollection requestArgs = new NameValueCollection
            {
                { "RequestMethod", "GetSession" },
                { "SessionID", token }
            };

            OSDMap response = WebUtil.PostToService(m_serverUrl, requestArgs);
            if (response["Success"].AsBoolean())
            {
                return true;
            }
            else
            {
                m_log.Warn("[SIMIAN AUTH CONNECTOR]: Could not verify session for " + principalID + ": " +
                    response["Message"].AsString());
            }

            return false;
        }

        public bool Release(UUID principalID, string token)
        {
            NameValueCollection requestArgs = new NameValueCollection
            {
                { "RequestMethod", "RemoveSession" },
                { "UserID", principalID.ToString() }
            };

            OSDMap response = WebUtil.PostToService(m_serverUrl, requestArgs);
            if (response["Success"].AsBoolean())
            {
                return true;
            }
            else
            {
                m_log.Warn("[SIMIAN AUTH CONNECTOR]: Failed to remove session for " + principalID + ": " +
                    response["Message"].AsString());
            }

            return false;
        }

        public bool SetPassword(UUID principalID, string passwd)
        {
            // Fetch the user name first
            NameValueCollection requestArgs = new NameValueCollection
            {
                { "RequestMethod", "GetUser" },
                { "UserID", principalID.ToString() }
            };

            OSDMap response = WebUtil.PostToService(m_serverUrl, requestArgs);
            if (response["Success"].AsBoolean() && response["User"] is OSDMap)
            {
                OSDMap userMap = (OSDMap)response["User"];
                string identifier = userMap["Name"].AsString();

                if (!String.IsNullOrEmpty(identifier))
                {
                    // Add/update the md5hash identity
                    requestArgs = new NameValueCollection
                    {
                        { "RequestMethod", "AddIdentity" },
                        { "Identifier", identifier },
                        { "Credential", "$1$" + Utils.MD5String(passwd) },
                        { "Type", "md5hash" },
                        { "UserID", principalID.ToString() }
                    };

                    response = WebUtil.PostToService(m_serverUrl, requestArgs);
                    bool success = response["Success"].AsBoolean();

                    if (!success)
                        m_log.WarnFormat("[SIMIAN AUTH CONNECTOR]: Failed to set password for {0} ({1})", identifier, principalID);

                    return success;
                }
            }
            else
            {
                m_log.Warn("[SIMIAN AUTH CONNECTOR]: Failed to retrieve identities for " + principalID + ": " +
                    response["Message"].AsString());
            }

            return false;
        }

        private string Authorize(UUID userID)
        {
            NameValueCollection requestArgs = new NameValueCollection
            {
                { "RequestMethod", "AddSession" },
                { "UserID", userID.ToString() }
            };

            OSDMap response = WebUtil.PostToService(m_serverUrl, requestArgs);
            if (response["Success"].AsBoolean())
                return response["SessionID"].AsUUID().ToString();
            else
                return String.Empty;
        }
    }
}
