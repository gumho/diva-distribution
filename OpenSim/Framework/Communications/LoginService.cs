/*
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
 *     * Neither the name of the OpenSim Project nor the
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
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using libsecondlife;
using libsecondlife.StructuredData;
using Nwc.XmlRpc;

using OpenSim.Framework.Communications.Cache;
using OpenSim.Framework.Console;
using OpenSim.Framework.Statistics;

namespace OpenSim.Framework.UserManagement
{
    public class LoginService
    {
        private static readonly log4net.ILog m_log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        protected string m_welcomeMessage = "Welcome to OpenSim";
        protected UserManagerBase m_userManager = null;
        protected Mutex m_loginMutex = new Mutex(false);     
        
        /// <summary>
        /// Used during login to send the skeleton of the OpenSim Library to the client.
        /// </summary>
        protected LibraryRootFolder m_libraryRootFolder;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="userManager"></param>
        /// <param name="libraryRootFolder"></param>
        /// <param name="welcomeMess"></param>
        public LoginService(UserManagerBase userManager, LibraryRootFolder libraryRootFolder, 
                            string welcomeMess)
        {
            m_userManager = userManager;
            m_libraryRootFolder = libraryRootFolder;
            
            if (welcomeMess != String.Empty)
            {
                m_welcomeMessage = welcomeMess;
            }
        }

        /// <summary>
        /// Called when we receive the client's initial XMLRPC login_to_simulator request message
        /// </summary>
        /// <param name="request">The XMLRPC request</param>
        /// <returns>The response to send</returns>
        public XmlRpcResponse XmlRpcLoginMethod(XmlRpcRequest request)
        {
            // Temporary fix
            m_loginMutex.WaitOne();
            try
            {
                //CFK: CustomizeResponse contains sufficient strings to alleviate the need for this.
                //CKF: m_log.Info("[LOGIN]: Attempting login now...");
                XmlRpcResponse response = new XmlRpcResponse();
                Hashtable requestData = (Hashtable) request.Params[0];

                bool GoodXML = (requestData.Contains("first") && requestData.Contains("last") &&
                                (requestData.Contains("passwd") || requestData.Contains("web_login_key")));
                bool GoodLogin = false;

                string startLocationRequest = "last";

                UserProfileData userProfile;
                LoginResponse logResponse = new LoginResponse();
                
                string firstname = String.Empty;
                string lastname = String.Empty;

                if (GoodXML)
                {
                    firstname = (string) requestData["first"];
                    lastname = (string) requestData["last"];
                    
                    m_log.InfoFormat(
                         "[LOGIN]: Received login request message from user {0} {1}", 
                         firstname, lastname);

                    if( requestData.Contains("version"))
                    {
                        string clientversion = (string)requestData["version"];
                        m_log.Info("[LOGIN]: Client version: " + clientversion);
                    }
                    
                    if (requestData.Contains("start"))
                    {
                        startLocationRequest = (string)requestData["start"];
                        m_log.Info("[LOGIN]: Client requested start location: " + (string)requestData["start"]);
                    }                    

                    userProfile = GetTheUser(firstname, lastname);
                    if (userProfile == null)
                    {
                        m_log.Info("[LOGIN]: Could not find a profile for " + firstname + " " + lastname);

                        return logResponse.CreateLoginFailedResponse();
                    }

                    if (requestData.Contains("passwd"))
                    {
                        string passwd = (string)requestData["passwd"];
                        GoodLogin = AuthenticateUser(userProfile, passwd);
                    }
                    else if (requestData.Contains("web_login_key"))
                    {
                        LLUUID webloginkey = null;
                        try
                        {
                            webloginkey = new LLUUID((string)requestData["web_login_key"]);
                        }
                        catch (System.Exception e)
                        {
                            m_log.InfoFormat(
                                 "[LOGIN]: Bad web_login_key: {0} for user {1} {2}, exception {3}", 
                                 requestData["web_login_key"], firstname, lastname, e);
                            
                            return logResponse.CreateFailedResponse();
                        }
                        GoodLogin = AuthenticateUser(userProfile, webloginkey);

                    }
                }
                else
                {
                    m_log.Info(
                        "[LOGIN]: login_to_simulator login message did not contain all the required data");
                    
                    return logResponse.CreateGridErrorResponse();
                }

                if (!GoodLogin)
                {
                    m_log.InfoFormat("[LOGIN]: User {0} {1} failed authentication", firstname, lastname);
                    
                    return logResponse.CreateLoginFailedResponse();
                }
                else
                {
                    // If we already have a session...
                    if (userProfile.currentAgent != null && userProfile.currentAgent.agentOnline)
                    {
                        //TODO: The following statements can cause trouble:
                        //      If agentOnline could not turn from true back to false normally
                        //      because of some problem, for instance, the crashment of server or client,
                        //      the user cannot log in any longer.
                        userProfile.currentAgent.agentOnline = false;
                        m_userManager.CommitAgent(ref userProfile);

                        // Reject the login
                        
                        m_log.InfoFormat(
                             "[LOGIN]: Notifying user {0} {1} that they are already logged in", 
                             firstname, lastname);
                        
                        return logResponse.CreateAlreadyLoggedInResponse();
                    }
                    // Otherwise...
                    // Create a new agent session
                    CreateAgent(userProfile, request);

                    try
                    {
                        LLUUID agentID = userProfile.UUID;

                        // Inventory Library Section
                        InventoryData inventData = CreateInventoryData(agentID);
                        ArrayList AgentInventoryArray = inventData.InventoryArray;

                        Hashtable InventoryRootHash = new Hashtable();
                        InventoryRootHash["folder_id"] = inventData.RootFolderID.ToString();
                        ArrayList InventoryRoot = new ArrayList();
                        InventoryRoot.Add(InventoryRootHash);
                        userProfile.rootInventoryFolderID = inventData.RootFolderID;

                        // Circuit Code
                        uint circode = (uint) (Util.RandomClass.Next());

                        logResponse.Lastname = userProfile.surname;
                        logResponse.Firstname = userProfile.username;
                        logResponse.AgentID = agentID.ToString();
                        logResponse.SessionID = userProfile.currentAgent.sessionID.ToString();
                        logResponse.SecureSessionID = userProfile.currentAgent.secureSessionID.ToString();
                        logResponse.InventoryRoot = InventoryRoot;
                        logResponse.InventorySkeleton = AgentInventoryArray;
                        logResponse.InventoryLibrary = GetInventoryLibrary();

                        Hashtable InventoryLibRootHash = new Hashtable();
                        InventoryLibRootHash["folder_id"] = "00000112-000f-0000-0000-000100bba000";
                        ArrayList InventoryLibRoot = new ArrayList();
                        InventoryLibRoot.Add(InventoryLibRootHash);
                        logResponse.InventoryLibRoot = InventoryLibRoot;

                        logResponse.InventoryLibraryOwner = GetLibraryOwner();
                        logResponse.CircuitCode = (Int32) circode;
                        //logResponse.RegionX = 0; //overwritten
                        //logResponse.RegionY = 0; //overwritten
                        logResponse.Home = "!!null temporary value {home}!!"; // Overwritten
                        //logResponse.LookAt = "\n[r" + TheUser.homeLookAt.X.ToString() + ",r" + TheUser.homeLookAt.Y.ToString() + ",r" + TheUser.homeLookAt.Z.ToString() + "]\n";
                        //logResponse.SimAddress = "127.0.0.1"; //overwritten
                        //logResponse.SimPort = 0; //overwritten
                        logResponse.Message = GetMessage();
                        logResponse.BuddList = ConvertFriendListItem(m_userManager.GetUserFriendList(agentID)); 

                        try
                        {
                            CustomiseResponse(logResponse, userProfile, startLocationRequest);
                        }
                        catch (Exception e)
                        {
                            m_log.Info("[LOGIN]: " + e.ToString());
                            return logResponse.CreateDeadRegionResponse();
                            //return logResponse.ToXmlRpcResponse();
                        }
                        CommitAgent(ref userProfile);
                        
                        // If we reach this point, then the login has successfully logged onto the grid
                        if (StatsManager.UserStats != null)
                            StatsManager.UserStats.AddSuccessfulLogin();
                        
                        m_log.InfoFormat(
                             "[LOGIN]: Authentication of user {0} {1} successful.  Sending response to client.",
                             firstname, lastname);
                        
                        return logResponse.ToXmlRpcResponse();
                    }
                    catch (Exception e)
                    {
                        m_log.Info("[LOGIN]: Login failed, exception" + e.ToString());
                    }
                }

                m_log.Info("[LOGIN]: Login failed.  Sending back blank XMLRPC response");
                return response;
            }
            finally
            {
                m_loginMutex.ReleaseMutex();
            }
        }

        public LLSD LLSDLoginMethod(LLSD request)
        {
            // Temporary fix
            m_loginMutex.WaitOne();

            try
            {
                bool GoodLogin = false;

                string startLocationRequest = "last";

                UserProfileData userProfile = null;
                LoginResponse logResponse = new LoginResponse();

                if (request.Type == LLSDType.Map)
                {
                    LLSDMap map = (LLSDMap)request;

                    if (map.ContainsKey("first") && map.ContainsKey("last") && map.ContainsKey("passwd"))
                    {
                        string firstname = map["first"].AsString();
                        string lastname = map["last"].AsString();
                        string passwd = map["passwd"].AsString();

                        if (map.ContainsKey("start"))
                        {
                            m_log.Info("[LOGIN]: StartLocation Requested: " + map["start"].AsString());
                            startLocationRequest = map["start"].AsString();
                        }

                        userProfile = GetTheUser(firstname, lastname);
                        if (userProfile == null)
                        {
                            m_log.Info("[LOGIN]: Could not find a profile for " + firstname + " " + lastname);

                            return logResponse.CreateLoginFailedResponseLLSD();
                        }

                        GoodLogin = AuthenticateUser(userProfile, passwd);
                    }
                }

                if (!GoodLogin)
                {
                    return logResponse.CreateLoginFailedResponseLLSD();
                }
                else
                {
                    // If we already have a session...
                    if (userProfile.currentAgent != null && userProfile.currentAgent.agentOnline)
                    {
                        userProfile.currentAgent = null;
                        m_userManager.CommitAgent(ref userProfile);

                        // Reject the login
                        return logResponse.CreateAlreadyLoggedInResponseLLSD();
                    }

                    // Otherwise...
                    // Create a new agent session
                    CreateAgent(userProfile, request);

                    try
                    {
                        LLUUID agentID = userProfile.UUID;

                        // Inventory Library Section
                        InventoryData inventData = CreateInventoryData(agentID);
                        ArrayList AgentInventoryArray = inventData.InventoryArray;

                        Hashtable InventoryRootHash = new Hashtable();
                        InventoryRootHash["folder_id"] = inventData.RootFolderID.ToString();
                        ArrayList InventoryRoot = new ArrayList();
                        InventoryRoot.Add(InventoryRootHash);
                        userProfile.rootInventoryFolderID = inventData.RootFolderID;

                        // Circuit Code
                        uint circode = (uint)(Util.RandomClass.Next());

                        logResponse.Lastname = userProfile.surname;
                        logResponse.Firstname = userProfile.username;
                        logResponse.AgentID = agentID.ToString();
                        logResponse.SessionID = userProfile.currentAgent.sessionID.ToString();
                        logResponse.SecureSessionID = userProfile.currentAgent.secureSessionID.ToString();
                        logResponse.InventoryRoot = InventoryRoot;
                        logResponse.InventorySkeleton = AgentInventoryArray;
                        logResponse.InventoryLibrary = GetInventoryLibrary();

                        Hashtable InventoryLibRootHash = new Hashtable();
                        InventoryLibRootHash["folder_id"] = "00000112-000f-0000-0000-000100bba000";
                        ArrayList InventoryLibRoot = new ArrayList();
                        InventoryLibRoot.Add(InventoryLibRootHash);
                        logResponse.InventoryLibRoot = InventoryLibRoot;

                        logResponse.InventoryLibraryOwner = GetLibraryOwner();
                        logResponse.CircuitCode = (Int32)circode;
                        //logResponse.RegionX = 0; //overwritten
                        //logResponse.RegionY = 0; //overwritten
                        logResponse.Home = "!!null temporary value {home}!!"; // Overwritten
                        //logResponse.LookAt = "\n[r" + TheUser.homeLookAt.X.ToString() + ",r" + TheUser.homeLookAt.Y.ToString() + ",r" + TheUser.homeLookAt.Z.ToString() + "]\n";
                        //logResponse.SimAddress = "127.0.0.1"; //overwritten
                        //logResponse.SimPort = 0; //overwritten
                        logResponse.Message = GetMessage();
                        logResponse.BuddList = ConvertFriendListItem(m_userManager.GetUserFriendList(agentID));

                        try
                        {
                            CustomiseResponse(logResponse, userProfile, startLocationRequest);
                        }
                        catch (Exception ex)
                        {
                            m_log.Info("[LOGIN]: " + ex.ToString());
                            return logResponse.CreateDeadRegionResponseLLSD();
                        }

                        CommitAgent(ref userProfile);
                        
                        // If we reach this point, then the login has successfully logged onto the grid
                        if (StatsManager.UserStats != null)
                            StatsManager.UserStats.AddSuccessfulLogin();                        

                        return logResponse.ToLLSDResponse();
                    }
                    catch (Exception ex)
                    {
                        m_log.Info("[LOGIN]: " + ex.ToString());
                        return logResponse.CreateFailedResponseLLSD();
                    }
                }
            }
            finally
            {
                m_loginMutex.ReleaseMutex();
            }
        }

        /// <summary>
        /// Customises the login response and fills in missing values.
        /// </summary>
        /// <param name="response">The existing response</param>
        /// <param name="theUser">The user profile</param>
        public virtual void CustomiseResponse(LoginResponse response, UserProfileData theUser, string startLocationRequest)
        {
        }

        public Hashtable ProcessHTMLLogin(Hashtable keysvals)
        {
            // Matches all unspecified characters
            // Currently specified,; lowercase letters, upper case letters, numbers, underline
            //    period, space, parens, and dash.

            Regex wfcut = new Regex("[^a-zA-Z0-9_\\.\\$ \\(\\)\\-]");
            
            Hashtable returnactions = new Hashtable();
            int statuscode = 200;

            string firstname = String.Empty;
            string lastname = String.Empty;
            string location = String.Empty;
            string region =String.Empty;
            string grid = String.Empty;
            string channel = String.Empty;
            string version = String.Empty;
            string lang = String.Empty;
            string password = String.Empty;
            string errormessages = String.Empty;

            // the client requires the HTML form field be named 'username'
            // however, the data it sends when it loads the first time is 'firstname'
            // another one of those little nuances.
            
            if (keysvals.Contains("firstname"))
                firstname = wfcut.Replace((string)keysvals["firstname"], String.Empty, 99999);

            if (keysvals.Contains("username"))
                firstname = wfcut.Replace((string)keysvals["username"], String.Empty, 99999);

            if (keysvals.Contains("lastname"))
                lastname = wfcut.Replace((string)keysvals["lastname"], String.Empty, 99999);

            if (keysvals.Contains("location"))
                location = wfcut.Replace((string)keysvals["location"], String.Empty, 99999);

            if (keysvals.Contains("region"))
                region = wfcut.Replace((string)keysvals["region"], String.Empty, 99999);

            if (keysvals.Contains("grid"))
                grid = wfcut.Replace((string)keysvals["grid"], String.Empty, 99999);

            if (keysvals.Contains("channel"))
                channel = wfcut.Replace((string)keysvals["channel"], String.Empty, 99999);

            if (keysvals.Contains("version"))
                version = wfcut.Replace((string)keysvals["version"], String.Empty, 99999);

            if (keysvals.Contains("lang"))
                lang = wfcut.Replace((string)keysvals["lang"], String.Empty, 99999);
           
            if (keysvals.Contains("password"))
                password = wfcut.Replace((string)keysvals["password"],  String.Empty, 99999);

            // load our login form.
            string loginform = GetLoginForm(firstname, lastname, location, region, grid, channel, version, lang, password, errormessages);

            if (keysvals.ContainsKey("show_login_form"))
            {
                UserProfileData user = GetTheUser(firstname, lastname);
                bool goodweblogin = false;

                if (user != null)
                    goodweblogin = AuthenticateUser(user, password);

                if (goodweblogin)
                {
                    LLUUID webloginkey = LLUUID.Random();
                    m_userManager.StoreWebLoginKey(user.UUID, webloginkey);
                    statuscode = 301;

                    string redirectURL = "about:blank?redirect-http-hack=" +
                        System.Web.HttpUtility.UrlEncode("secondlife:///app/login?first_name=" + firstname + "&last_name=" +
                                                         lastname +
                                                         "&location=" + location + "&grid=Other&web_login_key=" + webloginkey.ToString());
                    //m_log.Info("[WEB]: R:" + redirectURL);
                    returnactions["int_response_code"] = statuscode;
                    returnactions["str_redirect_location"] = redirectURL;
                    returnactions["str_response_string"] = "<HTML><BODY>GoodLogin</BODY></HTML>";
                }
                else
                {
                    errormessages = "The Username and password supplied did not match our records. Check your caps lock and try again";

                    loginform = GetLoginForm(firstname, lastname, location, region, grid, channel, version, lang, password, errormessages);
                    returnactions["int_response_code"] = statuscode;
                    returnactions["str_response_string"] = loginform;
                }
            }
            else
            {
                returnactions["int_response_code"] = statuscode;
                returnactions["str_response_string"] = loginform;
            }
            return returnactions;
        }

        public string GetLoginForm(string firstname, string lastname, string location, string region, 
                                   string grid, string channel, string version, string lang, 
                                   string password, string errormessages)
        {
            // inject our values in the form at the markers

            string loginform=String.Empty;
            string file = Path.Combine(Util.configDir(), "http_loginform.html");
            if (!File.Exists(file))
            {
                loginform = GetDefaultLoginForm();
            }
            else
            {
                StreamReader sr = File.OpenText(file);
                loginform = sr.ReadToEnd();
                sr.Close();
            }
            
            loginform = loginform.Replace("[$firstname]", firstname);
            loginform = loginform.Replace("[$lastname]", lastname);
            loginform = loginform.Replace("[$location]", location);
            loginform = loginform.Replace("[$region]", region);
            loginform = loginform.Replace("[$grid]", grid);
            loginform = loginform.Replace("[$channel]", channel);
            loginform = loginform.Replace("[$version]", version);
            loginform = loginform.Replace("[$lang]", lang);
            loginform = loginform.Replace("[$password]", password);
            loginform = loginform.Replace("[$errors]", errormessages);

            return loginform;
        }

        public string GetDefaultLoginForm()
        {
            string responseString =
                "<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.0 Transitional//EN\" \"http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd\">";
            responseString += "<html xmlns=\"http://www.w3.org/1999/xhtml\">";
            responseString += "<head>";
            responseString += "<meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\" />";
            responseString += "<meta http-equiv=\"cache-control\" content=\"no-cache\">";
            responseString += "<meta http-equiv=\"Pragma\" content=\"no-cache\">";
            responseString += "<title>OpenSim Login</title>";
            responseString += "<body><br />";
            responseString += "<div id=\"login_box\">";
                
            responseString += "<form action=\"/go.cgi\" method=\"GET\" id=\"login-form\">";

            responseString += "<div id=\"message\">[$errors]</div>";
            responseString += "<fieldset id=\"firstname\">";
            responseString += "<legend>First Name:</legend>";
            responseString += "<input type=\"text\" id=\"firstname_input\" size=\"15\" maxlength=\"100\" name=\"username\" value=\"[$firstname]\" />";
            responseString += "</fieldset>";
            responseString += "<fieldset id=\"lastname\">";
            responseString += "<legend>Last Name:</legend>";
            responseString += "<input type=\"text\" size=\"15\" maxlength=\"100\" name=\"lastname\" value=\"[$lastname]\" />";
            responseString += "</fieldset>";
            responseString += "<fieldset id=\"password\">";
            responseString += "<legend>Password:</legend>";
            responseString += "<table cellspacing=\"0\" cellpadding=\"0\" border=\"0\">";
            responseString += "<tr>";
            responseString += "<td colspan=\"2\"><input type=\"password\" size=\"15\" maxlength=\"100\" name=\"password\" value=\"[$password]\" /></td>";
            responseString += "</tr>";
            responseString += "<tr>";
            responseString += "<td valign=\"middle\"><input type=\"checkbox\" name=\"remember_password\" id=\"remember_password\" [$remember_password] style=\"margin-left:0px;\"/></td>";
            responseString += "<td><label for=\"remember_password\">Remember password</label></td>";
            responseString += "</tr>";
            responseString += "</table>";
            responseString += "</fieldset>";
            responseString += "<input type=\"hidden\" name=\"show_login_form\" value=\"FALSE\" />";
            responseString += "<input type=\"hidden\" name=\"method\" value=\"login\" />";
            responseString += "<input type=\"hidden\" id=\"grid\" name=\"grid\" value=\"[$grid]\" />";
            responseString += "<input type=\"hidden\" id=\"region\" name=\"region\" value=\"[$region]\" />";
            responseString += "<input type=\"hidden\" id=\"location\" name=\"location\" value=\"[$location]\" />";
            responseString += "<input type=\"hidden\" id=\"channel\" name=\"channel\" value=\"[$channel]\" />";
            responseString += "<input type=\"hidden\" id=\"version\" name=\"version\" value=\"[$version]\" />";
            responseString += "<input type=\"hidden\" id=\"lang\" name=\"lang\" value=\"[$lang]\" />";
            responseString += "<div id=\"submitbtn\">";
            responseString += "<input class=\"input_over\" type=\"submit\" value=\"Connect\" />";
            responseString += "</div>";
            responseString += "<div id=\"connecting\" style=\"visibility:hidden\"> Connecting...</div>";

            responseString += "<div id=\"helplinks\">";
            responseString += "<a href=\"#join now link\" target=\"_blank\"></a> | ";
            responseString += "<a href=\"#forgot password link\" target=\"_blank\"></a>";
            responseString += "</div>";

            responseString += "<div id=\"channelinfo\"> [$channel] | [$version]=[$lang]</div>";
            responseString += "</form>";
            responseString += "<script language=\"JavaScript\">";
            responseString += "document.getElementById('firstname_input').focus();";
            responseString += "</script>";
            responseString += "</div>";
            responseString += "</div>";
            responseString += "</body>";
            responseString += "</html>";

            return responseString;
        }

        /// <summary>
        /// Saves a target agent to the database
        /// </summary>
        /// <param name="profile">The users profile</param>
        /// <returns>Successful?</returns>
        public bool CommitAgent(ref UserProfileData profile)
        {
            return m_userManager.CommitAgent(ref profile);
        }

        /// <summary>
        /// Checks a user against it's password hash
        /// </summary>
        /// <param name="profile">The users profile</param>
        /// <param name="password">The supplied password</param>
        /// <returns>Authenticated?</returns>
        public virtual bool AuthenticateUser(UserProfileData profile, string password)
        {
            bool passwordSuccess = false;
            m_log.InfoFormat("[LOGIN]: Authenticating {0} {1} ({2})", profile.username, profile.surname, profile.UUID);

            // Web Login method seems to also occasionally send the hashed password itself

            // we do this to get our hash in a form that the server password code can consume
            // when the web-login-form submits the password in the clear (supposed to be over SSL!)
            if (!password.StartsWith("$1$"))
                password = "$1$" + Util.Md5Hash(password);

            password = password.Remove(0, 3); //remove $1$
            
            string s = Util.Md5Hash(password + ":" + profile.passwordSalt);
            // Testing...    
            //m_log.Info("[LOGIN]: SubHash:" + s + " userprofile:" + profile.passwordHash);
            //m_log.Info("[LOGIN]: userprofile:" + profile.passwordHash + " SubCT:" + password);

            passwordSuccess = (profile.passwordHash.Equals(s.ToString(), StringComparison.InvariantCultureIgnoreCase) 
                            || profile.passwordHash.Equals(password, StringComparison.InvariantCultureIgnoreCase));

            return passwordSuccess;
        }

        public virtual bool AuthenticateUser(UserProfileData profile, LLUUID webloginkey)
        {
            bool passwordSuccess = false;
            m_log.InfoFormat("[LOGIN]: Authenticating {0} {1} ({2})", profile.username, profile.surname, profile.UUID);

            // Match web login key unless it's the default weblogin key LLUUID.Zero
            passwordSuccess = ((profile.webLoginKey==webloginkey) && profile.webLoginKey != LLUUID.Zero);

            return passwordSuccess;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="profile"></param>
        /// <param name="request"></param>
        public void CreateAgent(UserProfileData profile, XmlRpcRequest request)
        {
            m_userManager.CreateAgent(profile, request);
        }

        public void CreateAgent(UserProfileData profile, LLSD request)
        {
            m_userManager.CreateAgent(profile, request);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="firstname"></param>
        /// <param name="lastname"></param>
        /// <returns></returns>
        public virtual UserProfileData GetTheUser(string firstname, string lastname)
        {
            return m_userManager.GetUserProfile(firstname, lastname);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        public virtual string GetMessage()
        {
            return m_welcomeMessage;
        }

        private LoginResponse.BuddyList ConvertFriendListItem(List<FriendListItem> LFL)
        {
            LoginResponse.BuddyList buddylistreturn = new LoginResponse.BuddyList();
            foreach (FriendListItem fl in LFL)
            {
                LoginResponse.BuddyList.BuddyInfo buddyitem = new LoginResponse.BuddyList.BuddyInfo(fl.Friend);
                buddyitem.BuddyID = fl.Friend;
                buddyitem.BuddyRightsHave = (int)fl.FriendListOwnerPerms;
                buddyitem.BuddyRightsGiven = (int) fl.FriendPerms;
                buddylistreturn.AddNewBuddy(buddyitem);
            }
            return buddylistreturn;
        }
        
        /// <summary>
        /// Converts the inventory library skeleton into the form required by the rpc request.
        /// </summary>
        /// <returns></returns>
        protected virtual ArrayList GetInventoryLibrary()
        {
            Dictionary<LLUUID, InventoryFolderImpl> rootFolders 
                = m_libraryRootFolder.RequestSelfAndDescendentFolders();
            ArrayList folderHashes = new ArrayList();
            
            foreach (InventoryFolderBase folder in rootFolders.Values)
            {
                Hashtable TempHash = new Hashtable();
                TempHash["name"] = folder.name;
                TempHash["parent_id"] = folder.parentID.ToString();
                TempHash["version"] = (Int32)folder.version;
                TempHash["type_default"] = (Int32)folder.type;
                TempHash["folder_id"] = folder.folderID.ToString();
                folderHashes.Add(TempHash);
            }
            
            return folderHashes;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        protected virtual ArrayList GetLibraryOwner()
        {
            //for now create random inventory library owner
            Hashtable TempHash = new Hashtable();
            TempHash["agent_id"] = "11111111-1111-0000-0000-000100bba000";
            ArrayList inventoryLibOwner = new ArrayList();
            inventoryLibOwner.Add(TempHash);
            return inventoryLibOwner;
        }

        protected virtual InventoryData CreateInventoryData(LLUUID userID)
        {
            AgentInventory userInventory = new AgentInventory();
            userInventory.CreateRootFolder(userID);

            ArrayList AgentInventoryArray = new ArrayList();
            Hashtable TempHash;
            foreach (InventoryFolder InvFolder in userInventory.InventoryFolders.Values)
            {
                TempHash = new Hashtable();
                TempHash["name"] = InvFolder.FolderName;
                TempHash["parent_id"] = InvFolder.ParentID.ToString();
                TempHash["version"] = (Int32) InvFolder.Version;
                TempHash["type_default"] = (Int32) InvFolder.DefaultType;
                TempHash["folder_id"] = InvFolder.FolderID.ToString();
                AgentInventoryArray.Add(TempHash);
            }

            return new InventoryData(AgentInventoryArray, userInventory.InventoryRoot.FolderID);
        }

        public class InventoryData
        {
            public ArrayList InventoryArray = null;
            public LLUUID RootFolderID = LLUUID.Zero;

            public InventoryData(ArrayList invList, LLUUID rootID)
            {
                InventoryArray = invList;
                RootFolderID = rootID;
            }
        }
    }
}
