﻿/**
 * Copyright (c) Crista Lopes (aka Diva). All rights reserved.
 * 
 * Redistribution and use in source and binary forms, with or without modification, 
 * are permitted provided that the following conditions are met:
 * 
 *     * Redistributions of source code must retain the above copyright notice, 
 *       this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright notice, 
 *       this list of conditions and the following disclaimer in the documentation 
 *       and/or other materials provided with the distribution.
 *     * Neither the name of the Organizations nor the names of Individual
 *       Contributors may be used to endorse or promote products derived from 
 *       this software without specific prior written permission.
 * 
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND 
 * ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES 
 * OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL 
 * THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, 
 * EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE 
 * GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED 
 * AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING 
 * NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED 
 * OF THE POSSIBILITY OF SUCH DAMAGE.
 * 
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Xml;

using OpenSim.Framework.Servers.HttpServer;

using log4net;

using Diva.Wifi;

namespace Diva.Wifi
{
    public class WifiUtils
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public static string DocsPath = System.IO.Path.Combine("..", "WifiPages");

        public static string GetContentType(string resource)
        {
            if (resource.ToLower().EndsWith(".jpg"))
                return "image/jpeg";
            if (resource.ToLower().EndsWith(".gif"))
                return "image/gif";
            if (resource.ToLower().EndsWith(".css"))
                return "text/css";
            return "text/html";
        }

        public static byte[] ReadBinaryResource(string resourceName)
        {
            try
            {
                using (BinaryReader sr = new BinaryReader(File.Open(resourceName, FileMode.Open)))
                {
                    byte[] buffer = new byte[32768];
                    using (MemoryStream ms = new MemoryStream())
                    {
                        while (true)
                        {
                            int read = sr.Read(buffer, 0, buffer.Length);
                            if (read <= 0)
                                return ms.ToArray();
                            ms.Write(buffer, 0, read);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                // Let the user know what went wrong.
                m_log.DebugFormat("[Wifi]: Exception {0}", e.Message);
            }

            return new byte[0];
        }

        public static string ReadTextResource(string resourceName)
        {
            String buffer = String.Empty;
            try
            {
                // Create an instance of StreamReader to read from a file.
                // The using statement also closes the StreamReader.
                using (StreamReader sr = new StreamReader(resourceName))
                {
                    String line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        buffer += line;
                    }
                }
            }
            catch (Exception e)
            {
                // Let the user know what went wrong.
                m_log.DebugFormat("[Wifi]: Exception {0}", e.Message);
            }

            return buffer;
        }
        public static byte[] StringToBytes(string str)
        {
            return Encoding.UTF8.GetBytes(str);
        }

        public static Request CreateRequest(string resource, OSHttpRequest httpRequest)
        {
            Request request = new Request();
            request.Resource = resource;
            request.Cookies = httpRequest.Cookies;
            request.IPEndPoint = httpRequest.RemoteIPEndPoint;
            request.Query = httpRequest.Query;

            return request;
        }

        public static byte[] SuccessResult()
        {
            XmlDocument doc = new XmlDocument();

            XmlNode xmlnode = doc.CreateNode(XmlNodeType.XmlDeclaration,
                    "", "");

            doc.AppendChild(xmlnode);

            XmlElement rootElement = doc.CreateElement("", "ServerResponse",
                    "");

            doc.AppendChild(rootElement);

            XmlElement result = doc.CreateElement("", "result", "");
            result.AppendChild(doc.CreateTextNode("Success"));

            rootElement.AppendChild(result);

            return DocToBytes(doc);
        }

        public static byte[] FailureResult()
        {
            XmlDocument doc = new XmlDocument();

            XmlNode xmlnode = doc.CreateNode(XmlNodeType.XmlDeclaration,
                    "", "");

            doc.AppendChild(xmlnode);

            XmlElement rootElement = doc.CreateElement("", "ServerResponse",
                    "");

            doc.AppendChild(rootElement);

            XmlElement result = doc.CreateElement("", "result", "");
            result.AppendChild(doc.CreateTextNode("Failure"));

            rootElement.AppendChild(result);

            return DocToBytes(doc);
        }

        public static byte[] DocToBytes(XmlDocument doc)
        {
            MemoryStream ms = new MemoryStream();
            XmlTextWriter xw = new XmlTextWriter(ms, null);
            xw.Formatting = Formatting.Indented;
            doc.WriteTo(xw);
            xw.Flush();

            return ms.ToArray();
        }

    }
}
