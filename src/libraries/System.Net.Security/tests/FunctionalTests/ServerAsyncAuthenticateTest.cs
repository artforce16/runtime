// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.ComponentModel;
using System.Net.Sockets;
using System.Net.Test.Common;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

using Xunit;
using Xunit.Abstractions;

namespace System.Net.Security.Tests
{
    using Configuration = System.Net.Test.Common.Configuration;

    public class ServerAsyncAuthenticateTest : IDisposable
    {
        private readonly ITestOutputHelper _log;
        private readonly ITestOutputHelper _logVerbose;
        private readonly X509Certificate2 _serverCertificate;

        public ServerAsyncAuthenticateTest(ITestOutputHelper output)
        {
            _log = output;
            _logVerbose = VerboseTestLogging.GetInstance();
            _serverCertificate = Configuration.Certificates.GetServerCertificate();
        }

        public void Dispose()
        {
            _serverCertificate.Dispose();
        }

        [Theory]
        [ClassData(typeof(SslProtocolSupport.SupportedSslProtocolsTestData))]
        public async Task ServerAsyncAuthenticate_EachSupportedProtocol_Success(SslProtocols protocol)
        {
            await ServerAsyncSslHelper(protocol, protocol);
        }

        [Theory]
        [MemberData(nameof(ProtocolMismatchData))]
        public async Task ServerAsyncAuthenticate_MismatchProtocols_Fails(
            SslProtocols serverProtocol,
            SslProtocols clientProtocol,
            Type expectedException)
        {
            Exception e = await Record.ExceptionAsync(
                () =>
                {
                    return ServerAsyncSslHelper(
                        serverProtocol,
                        clientProtocol,
                        expectedToFail: true);
                });

            Assert.NotNull(e);
            Assert.IsAssignableFrom(expectedException, e);
        }

        [Theory]
        [ClassData(typeof(SslProtocolSupport.SupportedSslProtocolsTestData))]
        public async Task ServerAsyncAuthenticate_AllClientVsIndividualServerSupportedProtocols_Success(
            SslProtocols serverProtocol)
        {
            await ServerAsyncSslHelper(SslProtocolSupport.SupportedSslProtocols, serverProtocol);
        }

        public static IEnumerable<object[]> ProtocolMismatchData()
        {
            if (PlatformDetection.SupportsSsl3)
            {
#pragma warning disable 0618
                yield return new object[] { SslProtocols.Ssl3, SslProtocols.Tls12, typeof(Exception) };
                if (PlatformDetection.SupportsSsl2)
                {
                    yield return new object[] { SslProtocols.Ssl2, SslProtocols.Ssl3, typeof(Exception) };
                    yield return new object[] { SslProtocols.Ssl2, SslProtocols.Tls12, typeof(Exception) };
                }
#pragma warning restore 0618
            }

            yield return new object[] { SslProtocols.Tls, SslProtocols.Tls11, typeof(AuthenticationException) };
            yield return new object[] { SslProtocols.Tls, SslProtocols.Tls12, typeof(AuthenticationException) };
            yield return new object[] { SslProtocols.Tls11, SslProtocols.Tls, typeof(AuthenticationException) };
            yield return new object[] { SslProtocols.Tls11, SslProtocols.Tls12, typeof(AuthenticationException) };
            yield return new object[] { SslProtocols.Tls12, SslProtocols.Tls, typeof(AuthenticationException) };
            yield return new object[] { SslProtocols.Tls12, SslProtocols.Tls11, typeof(AuthenticationException) };
        }

        #region Helpers

        private async Task ServerAsyncSslHelper(
            SslProtocols clientSslProtocols,
            SslProtocols serverSslProtocols,
            bool expectedToFail = false)
        {
            _log.WriteLine(
                "Server: " + serverSslProtocols + "; Client: " + clientSslProtocols +
                " expectedToFail: " + expectedToFail);

            (NetworkStream clientStream, NetworkStream serverStream) = TestHelper.GetConnectedTcpStreams();

            using (SslStream sslServerStream = new SslStream(
                serverStream,
                false,
                AllowEmptyClientCertificate))
            using (SslStream sslClientStream = new SslStream(
                clientStream,
                false,
                delegate {
                    // Allow any certificate from the server.
                    // Note that simply ignoring exceptions from AuthenticateAsClientAsync() is not enough
                    // because in Mono, certificate validation is performed during the handshake and a failure
                    // would result in the connection being terminated before the handshake completed, thus
                    // making the server-side AuthenticateAsServerAsync() fail as well.
                    return true;
                }))
            {
                string serverName = _serverCertificate.GetNameInfo(X509NameType.SimpleName, false);

                _log.WriteLine("Connected on {0} {1} ({2} {3})", clientStream.Socket.LocalEndPoint, clientStream.Socket.RemoteEndPoint, clientStream.Socket.Handle, serverStream.Socket.Handle);
                _log.WriteLine("client SslStream#{0} server SslStream#{1}", sslClientStream.GetHashCode(),  sslServerStream.GetHashCode());

                _logVerbose.WriteLine("ServerAsyncAuthenticateTest.AuthenticateAsClientAsync start.");
                Task clientAuthentication = sslClientStream.AuthenticateAsClientAsync(
                    serverName,
                    null,
                    clientSslProtocols,
                    false);

                _logVerbose.WriteLine("ServerAsyncAuthenticateTest.AuthenticateAsServerAsync start.");
                Task serverAuthentication = sslServerStream.AuthenticateAsServerAsync(
                    _serverCertificate,
                    true,
                    serverSslProtocols,
                    false);

                try
                {
                    await clientAuthentication.TimeoutAfter(TestConfiguration.PassingTestTimeoutMilliseconds);
                    _logVerbose.WriteLine("ServerAsyncAuthenticateTest.clientAuthentication complete.");
                }
                catch (Exception ex)
                {
                    // Ignore client-side errors: we're only interested in server-side behavior.
                    _log.WriteLine("Client exception : " + ex);
                    clientStream.Socket.Shutdown(SocketShutdown.Send);
                }

                await serverAuthentication.TimeoutAfter(TestConfiguration.PassingTestTimeoutMilliseconds);
                _logVerbose.WriteLine("ServerAsyncAuthenticateTest.serverAuthentication complete.");

                _log.WriteLine(
                    "Server({0}) authenticated with encryption cipher: {1} {2}-bit strength",
                    serverStream.Socket.LocalEndPoint,
                    sslServerStream.CipherAlgorithm,
                    sslServerStream.CipherStrength);

                Assert.True(
                    sslServerStream.CipherAlgorithm != CipherAlgorithmType.Null,
                    "Cipher algorithm should not be NULL");

                Assert.True(sslServerStream.CipherStrength > 0, "Cipher strength should be greater than 0");
            }
        }

        // The following method is invoked by the RemoteCertificateValidationDelegate.
        private bool AllowEmptyClientCertificate(
              object sender,
              X509Certificate certificate,
              X509Chain chain,
              SslPolicyErrors sslPolicyErrors)
        {
            Assert.True(
                (sslPolicyErrors & SslPolicyErrors.RemoteCertificateNotAvailable) == SslPolicyErrors.RemoteCertificateNotAvailable,
                "Client didn't supply a cert, the server required one, yet sslPolicyErrors is " + sslPolicyErrors);
            return true;  // allow everything
        }

        #endregion Helpers
    }
}
