﻿// Copyright (c) 2015-2018, Sonatype Inc.
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are met:
//     * Redistributions of source code must retain the above copyright
//       notice, this list of conditions and the following disclaimer.
//     * Redistributions in binary form must reproduce the above copyright
//       notice, this list of conditions and the following disclaimer in the
//       documentation and/or other materials provided with the distribution.
//     * Neither the name of Sonatype, OSS Index, nor the
//       names of its contributors may be used to endorse or promote products
//       derived from this software without specific prior written permission.
// 
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
// ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
// WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
// DISCLAIMED. IN NO EVENT SHALL SONATYPE BE LIABLE FOR ANY
// DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
// (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
// LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
// ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
// SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

using PackageUrl;
using RestSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Cache;
using System.Reflection;
using System.Runtime.Caching;
using System.Text;
using System.Threading.Tasks;

namespace NugetAuditor.Lib.OSSIndex
{
    internal class ApiClient : ApiClientBase, IApiClient
    {
        private int _pageSize = 100;

        private FileCache cache = null;

        private long cacheExpiration { get; set; } = 43200; // Seconds in 12 hours

        public ApiClient()
            : this(new HttpRequestCachePolicy(HttpRequestCacheLevel.Default))
        {
            initCache();
        }

        public ApiClient(HttpRequestCachePolicy cachePolicy)
            : base("https://ossindex.sonatype.org/api/v3", cachePolicy)
        {
            initCache();
        }

        private void initCache()
        {
            // Get an appropriate place for the cache and initialize it
            var directory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string path = Path.Combine(directory, "OSSIndex", "cache");
            cache = new FileCache(path, new ObjectBinder());
        }

        private void BeforeSerialization(IRestResponse response)
        {
            if (response.StatusCode >= HttpStatusCode.BadRequest)
            {
                throw new ApiClientException(string.Format("Unexpected response status {0}", (int)response.StatusCode));
            }
        }

        public IList<Package> SearchPackages(IEnumerable<PackageURL> inCoords)
        {
            var result = new List<Package>(inCoords.Count());

            List<PackageURL> useCoords = new List<PackageURL>();

            foreach (PackageURL purl in inCoords)
            {
                Package cachedPkg = (Package)cache[purl.ToString()];
                if (cachedPkg != null)
                {
                    long now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
                    long diff = now - cachedPkg.CachedAt;
                    if (diff < cacheExpiration)
                    {
                        result.Add(cachedPkg);
                    }
                    else
                    {
                        useCoords.Add(purl);
                    }
                }
                else
                {
                    useCoords.Add(purl);
                }
            }

            IEnumerable<PackageURL> useEnumerable = useCoords;

            while (useEnumerable.Any())
            {
                var request = new RestRequest(Method.POST);

                ComponentReport report = new ComponentReport();
                report.coordinates = useEnumerable.Select(x => x.ToString());

                request.Resource = "component-report";
                request.RequestFormat = DataFormat.Json;
                request.OnBeforeDeserialization = BeforeSerialization;
                request.AddBody(report);

                var response = Execute<PackageResponse>(request);

                if (response.ResponseStatus == ResponseStatus.Error)
                {
                    throw new ApiClientTransportException(response.ErrorMessage, response.ErrorException);
                }

                foreach (Package pkg in response.Data)
                {
                    pkg.CachedAt = DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
                    cache[pkg.Coordinates] = pkg;
                    result.Add(pkg);
                }

                useEnumerable = useEnumerable.Skip(this._pageSize);
            }

            return result;
        }
    }
    /** https://github.com/acarteas/FileCache
     */
    public sealed class ObjectBinder : System.Runtime.Serialization.SerializationBinder
    {
        public override Type BindToType(string assemblyName, string typeName)
        {
            Type typeToDeserialize = null;
            String currentAssembly = Assembly.GetExecutingAssembly().FullName;

            // In this case we are always using the current assembly
            assemblyName = currentAssembly;

            // Get the type using the typeName and assemblyName
            typeToDeserialize = Type.GetType(String.Format("{0}, {1}",
            typeName, assemblyName));

            return typeToDeserialize;
        }
    }
}
