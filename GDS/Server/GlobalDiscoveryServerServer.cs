/* Copyright (c) 1996-2016, OPC Foundation. All rights reserved.

   The source code in this file is covered under a dual-license scenario:
     - RCL: for OPC Foundation members in good-standing
     - GPL V2: everybody else

   RCL license terms accompanied with this source code. See http://opcfoundation.org/License/RCL/1.00/

   GNU General Public License as published by the Free Software Foundation;
   version 2 of the License are accompanied with this source code. See http://opcfoundation.org/License/GPLv2

   This source code is distributed in the hope that it will be useful,
   but WITHOUT ANY WARRANTY; without even the implied warranty of
   MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
*/

using System;
using System.Collections.Generic;
using System.Text;
using System.Xml;
using System.IdentityModel.Selectors;
using System.IdentityModel.Tokens;
using System.Security.Principal;
using System.Security.Cryptography.X509Certificates;
using System.ServiceModel.Security;
using System.Runtime.InteropServices;
using System.Threading;
using Opc.Ua;
using Opc.Ua.Server;
using Opc.Ua.Gds;

namespace Opc.Ua.GdsServer
{
    /// <summary>
    /// Implements a basic Server.
    /// </summary>
    /// <remarks>
    /// Each server instance must have one instance of a StandardServer object which is
    /// responsible for reading the configuration file, creating the endpoints and dispatching
    /// incoming requests to the appropriate handler.
    /// 
    /// This sub-class specifies non-configurable metadata such as Product Name and initializes
    /// the DataTypesNodeManager which provides access to the data exposed by the Server.
    /// </remarks>
    public partial class GlobalDiscoveryServerServer : StandardServer
    {
        #region Private Fields
        private object m_lock = new object();
        private X509CertificateValidator m_certificateValidator;
        private Dictionary<uint, ImpersonationContext> m_contexts = new Dictionary<uint, ImpersonationContext>();
        private Thread m_browseNetworkThread;

        private Dictionary<string, IList<NodeId>> m_scopes;
        private Dictionary<string, IList<NodeId>> m_users;
        private Dictionary<string, IList<NodeId>> m_roles;

        private NodeId PubSubNormalNodeId { get; set; }
        private NodeId PubSubSecureNodeId { get; set; }
        #endregion 

        public GlobalDiscoveryServerServer()
        {
            ServerCapabilities = new string[] { ServerCapability.GlobalDiscoveryServer };

            m_scopes = new Dictionary<string, IList<NodeId>>();
            m_users = new Dictionary<string, IList<NodeId>>();
            m_roles = new Dictionary<string, IList<NodeId>>();
        }

        #region Overridden Methods
        /// <summary>
        /// Initializes the server before it starts up.
        /// </summary>
        /// <remarks>
        /// This method is called before any startup processing occurs. The sub-class may update the 
        /// configuration object or do any other application specific startup tasks.
        /// </remarks>
        protected override void OnServerStarting(ApplicationConfiguration configuration)
        {
            base.OnServerStarting(configuration);
            
            // it is up to the application to decide how to validate user identity tokens.
            // this function creates validators for X509 identity tokens.
            CreateUserIdentityValidators(configuration);
        }

        /// <summary>
        /// Called after the server has been started.
        /// </summary>
        protected override void OnServerStarted(IServerInternal server)
        {
            base.OnServerStarted(server);

            // request notifications when the user identity is changed. all valid users are accepted by default.
            server.SessionManager.ImpersonateUser += SessionManager_ImpersonateUser;

            // validate session-less requests.
            server.SessionManager.ValidateSessionLessRequest += SessionManager_ValidateSessionLessRequest;

            m_browseNetworkThread = new Thread(OnBrowseNetwork);
            m_browseNetworkThread.IsBackground = true;
            m_browseNetworkThread.Start(null);

            DateTime now = DateTime.UtcNow;
            now = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);

            server.DiagnosticsNodeManager.AddSecurityGroup(
                "Group1",
                SecurityPolicies.Basic128Rsa15,
                now,
                TimeSpan.FromMinutes(10),
                new NodeId[] { PubSubNormalNodeId, PubSubSecureNodeId });

            server.DiagnosticsNodeManager.AddSecurityGroup(
                "Group2",
                SecurityPolicies.Basic256Sha256,
                now,
                TimeSpan.FromMinutes(60),
                new NodeId[] { PubSubSecureNodeId });

            // set up mapping rules for scopes.
            m_scopes.Add("UAServer", new NodeId[] { ObjectIds.WellKnownRole_Operator });
            m_scopes.Add("UAPubSub", new NodeId[] { PubSubNormalNodeId });

            // set up mapping rules for known users.
            m_users.Add("gdsadmin", new NodeId[] { ObjectIds.WellKnownRole_SecurityAdmin });
            m_users.Add("appadmin", new NodeId[] { ObjectIds.WellKnownRole_Engineer });
            m_users.Add("appuser", new NodeId[] { ObjectIds.WellKnownRole_Operator });

            // set up mapping rules for server defined groups.
            m_roles.Add("publishers", new NodeId[] { PubSubSecureNodeId });
            m_roles.Add("subscribers", new NodeId[] { PubSubSecureNodeId });
            m_roles.Add("applications", new NodeId[] { ObjectIds.WellKnownRole_Operator, PubSubNormalNodeId });
        }

        /// <summary>
        /// Called before the server stops
        /// </summary>
        protected override void OnServerStopping()
        {
            m_browseNetworkThread.Interrupt();
            base.OnServerStopping();
        }

        public ApplicationRecordDataType FindApplication(string applicationUri)
        {
            var database = new ApplicationsDatabase();

            var records = database.FindApplications(applicationUri);

            if (records == null || records.Length == 0)
            {
                return null;
            }

            return records[0];
        }

        private void OnBrowseNetwork(object state)
        {
            try
            {
                var configuration = Configuration.ParseExtension<GlobalDiscoveryServerConfiguration>();

                if (configuration != null && configuration.KnownHostNames != null)
                {
                    while (true)
                    {
                        Thread.Sleep(10000);

                        Opc.Ua.Gds.LocalDiscoveryServer lds = new Gds.LocalDiscoveryServer(this.Configuration);

                        var database = new ApplicationsDatabase();

                        foreach (string hostname in configuration.KnownHostNames)
                        {
                            try
                            {
                                var applications = lds.FindServers("opc.tcp://" + hostname + ":4840", null);

                                foreach (var application in applications)
                                {
                                    if (application.ApplicationType == ApplicationType.DiscoveryServer)
                                    {
                                        continue;
                                    }

                                    var records = database.FindApplications(application.ApplicationUri);

                                    if (records == null || records.Length == 0)
                                    {
                                        database.RegisterApplication(new Gds.ApplicationRecordDataType()
                                        {
                                            ApplicationUri = application.ApplicationUri,
                                            ApplicationNames = new LocalizedText[] { application.ApplicationName },
                                            ApplicationType = application.ApplicationType,
                                            DiscoveryUrls = application.DiscoveryUrls,
                                            ProductUri = application.ProductUri,
                                            ServerCapabilities = new string[] { ServerCapability.NoInformation }
                                        });
                                    }
                                }
                            }
                            catch (Exception e)
                            {
                                Utils.Trace(e, "Unexpected error browsing LDS on host: " + hostname);
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Utils.Trace(e, "Unexpected error browsing network.");
            }
        }

        /// <summary>
        /// Creates the node managers for the server.
        /// </summary>
        /// <remarks>
        /// This method allows the sub-class create any additional node managers which it uses. The SDK
        /// always creates a CoreNodeManager which handles the built-in nodes defined by the specification.
        /// Any additional NodeManagers are expected to handle application specific nodes.
        /// </remarks>
        protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
        {
            Utils.Trace("Creating the Node Managers.");

            List<INodeManager> nodeManagers = new List<INodeManager>();

            // create the custom node managers.
            var nodeManager = new ApplicationsNodeManager(server, configuration);
            nodeManagers.Add(nodeManager);
            nodeManager.PubSubNormalNodeId = PubSubNormalNodeId = new NodeId(100000, nodeManager.NamespaceIndex);
            nodeManager.PubSubSecureNodeId = PubSubSecureNodeId = new NodeId(100001, nodeManager.NamespaceIndex);

            // create master node manager.
            var mnm = new MasterNodeManager(server, configuration, null, nodeManagers.ToArray());
            return mnm;
        }

        /// <summary>
        /// Loads the non-configurable properties for the application.
        /// </summary>
        /// <remarks>
        /// These properties are exposed by the server but cannot be changed by administrators.
        /// </remarks>
        protected override ServerProperties LoadServerProperties()
        {
            ServerProperties properties = new ServerProperties();

            properties.ManufacturerName = "Some Company Inc";
            properties.ProductName      = "Global Discovery Server";
            properties.ProductUri       = "http://somecompany.com/GlobalDiscoveryServer";
            properties.SoftwareVersion  = Utils.GetAssemblySoftwareVersion();
            properties.BuildNumber      = Utils.GetAssemblyBuildNumber();
            properties.BuildDate        = Utils.GetAssemblyTimestamp();

            return properties;
        }

        /// <summary>
        /// This method is called at the being of the thread that processes a request.
        /// </summary>
        protected override OperationContext ValidateRequest(RequestHeader requestHeader, RequestType requestType)
        {
            OperationContext context = base.ValidateRequest(requestHeader, requestType);

            if (requestType == RequestType.Write)
            {
                // reject all writes if no user provided.
                if (context.UserIdentity.TokenType == UserTokenType.Anonymous)
                {
                    // construct translation object with default text.
                    TranslationInfo info = new TranslationInfo(
                        "NoWriteAllowed",
                        "en-US",
                        "Must provide a valid windows user before calling write.");

                    // create an exception with a vendor defined sub-code.
                    throw new ServiceResultException(new ServiceResult(
                        StatusCodes.BadUserAccessDenied,
                        "NoWriteAllowed",
                        Opc.Ua.Gds.Namespaces.OpcUaGds,
                        new LocalizedText(info)));
                }

                SecurityToken securityToken = context.UserIdentity.GetSecurityToken();

                // check for a user name token.
                UserNameSecurityToken userNameToken = securityToken as UserNameSecurityToken;

                if (userNameToken != null)
                {
                    ImpersonationContext impersonationContext = UserIdentity.LogonUser(userNameToken, false);

                    lock (m_lock)
                    {
                        m_contexts.Add(context.RequestId, impersonationContext);
                    }
                }
            }

            return context;
        }

        /// <summary>
        /// This method is called in a finally block at the end of request processing (i.e. called even on exception).
        /// </summary>
        protected override void OnRequestComplete(OperationContext context)
        {
            ImpersonationContext impersonationContext = null;

            lock (m_lock)
            {
                if (m_contexts.TryGetValue(context.RequestId, out impersonationContext))
                {
                    m_contexts.Remove(context.RequestId);
                }
            }

            if (impersonationContext != null)
            {
                impersonationContext.Dispose();
            }

            base.OnRequestComplete(context);
        }

        /// <summary>
        /// Creates the objects used to validate the user identity tokens supported by the server.
        /// </summary>
        private void CreateUserIdentityValidators(ApplicationConfiguration configuration)
        {
            for (int ii = 0; ii < configuration.ServerConfiguration.UserTokenPolicies.Count; ii++)
            {
                UserTokenPolicy policy = configuration.ServerConfiguration.UserTokenPolicies[ii];

                // create a validator for a certificate token policy.
                if (policy.TokenType == UserTokenType.Certificate)
                {
                    // the name of the element in the configuration file.
                    XmlQualifiedName qname = new XmlQualifiedName(policy.PolicyId, Opc.Ua.Namespaces.OpcUa);

                    // find the location of the trusted issuers.
                    CertificateTrustList trustedIssuers = configuration.ParseExtension<CertificateTrustList>(qname);

                    if (trustedIssuers == null)
                    {
                        Utils.Trace(
                            (int)Utils.TraceMasks.Error,
                            "Could not load CertificateTrustList for UserTokenPolicy {0}",
                            policy.PolicyId);

                        continue;
                    }

                    // trusts any certificate in the trusted people store.
                    m_certificateValidator = X509CertificateValidator.PeerTrust;
                }
            }
        }

        private IUserIdentity ValidateJwt(JwtEndpointParameters parameters, string jwt)
        {
            IUserIdentity identity = JwtUtils.ValidateToken(new Uri(parameters.AuthorityUrl), null, null, Configuration.ApplicationUri, jwt);

            JwtSecurityToken jwtToken = identity.GetSecurityToken() as JwtSecurityToken;

            if (jwtToken == null)
            {
                throw new ServiceResultException(StatusCodes.BadInternalError);
            }

            List<NodeId> roles = new List<NodeId>();

            // valid token means the user has been authenticated.
            roles.Add(ObjectIds.WellKnownRole_AuthenticatedUser);

            // find additional roles based on the scopes in the role.
            foreach (var claim in jwtToken.Claims)
            {
                switch (claim.Type)
                {
                    case "roles":
                    {
                        var fields = claim.Value.ToString().ToLowerInvariant().Split();

                        IList<NodeId> mappings = null;

                        foreach (var field in fields)
                        {
                            if (m_roles.TryGetValue(field, out mappings))
                            {
                                foreach (var mapping in mappings)
                                {
                                    if (!roles.Contains(mapping))
                                    {
                                        roles.Add(mapping);
                                    }
                                }
                            }
                        }

                        break;
                    }

                    case "scp":
                    {
                        var value = claim.Value.ToString().ToLowerInvariant();
                        var scopes = value.Split();

                        IList<NodeId> rolesForScope = null;

                        foreach (var scope in scopes)
                        {
                            if (m_scopes.TryGetValue(scope, out rolesForScope))
                            {
                                foreach (var roleForScope in rolesForScope)
                                {
                                    if (!roles.Contains(roleForScope))
                                    {
                                        roles.Add(roleForScope);
                                    }
                                }
                            }
                        }

                        break;
                    }
                }
            }

            return new RoleBasedIdentity(identity, roles);
        }

        private void SessionManager_ValidateSessionLessRequest(object sender, ValidateSessionLessRequestEventArgs e)
        {
            // check for encryption.
            var endpoint = SecureChannelContext.Current.EndpointDescription;

            if (endpoint == null || (endpoint.SecurityPolicyUri == SecurityPolicies.None && !endpoint.EndpointUrl.StartsWith(Uri.UriSchemeHttps)) || endpoint.SecurityMode == MessageSecurityMode.Sign)
            {
                e.Error = StatusCodes.BadSecurityModeInsufficient;
                return;
            }

            // find user token policy.
            JwtEndpointParameters parameters = null;

            foreach (var policy in endpoint.UserIdentityTokens)
            {
                if (policy.IssuedTokenType == Profiles.JwtUserToken)
                {
                    parameters = new JwtEndpointParameters();
                    parameters.FromJson(policy.IssuerEndpointUrl);
                    break;
                }
            }

            if (parameters == null)
            {
                e.Error = StatusCodes.BadIdentityTokenRejected;
                return;
            }

            // check authentication token.
            if (NodeId.IsNull(e.AuthenticationToken) || e.AuthenticationToken.IdType != IdType.String || e.AuthenticationToken.NamespaceIndex != 0)
            {
                e.Error = StatusCodes.BadIdentityTokenInvalid;
                return;
            }

            // validate token.
            string jwt = (string)e.AuthenticationToken.Identifier;

            var identity = ValidateJwt(parameters, jwt);
            Utils.Trace("JSON Web Token Accepted: {0}", identity.DisplayName);

            e.Identity = identity;
            e.Error = ServiceResult.Good;
        }

        /// <summary>
        /// Called when a client tries to change its user identity.
        /// </summary>
        private void SessionManager_ImpersonateUser(Session session, ImpersonateEventArgs args)
        {
            // check for an issued token.
            IssuedIdentityToken issuedToken = args.NewIdentity as IssuedIdentityToken;

            if (issuedToken != null)
            {
                if (args.UserTokenPolicy.IssuedTokenType == Profiles.JwtUserToken)
                {
                    JwtEndpointParameters parameters = new JwtEndpointParameters();
                    parameters.FromJson(args.UserTokenPolicy.IssuerEndpointUrl);
                    var jwt = new UTF8Encoding().GetString(issuedToken.DecryptedTokenData);
                    var identity = ValidateJwt(parameters, jwt);
                    Utils.Trace("JSON Web Token Accepted: {0}", identity.DisplayName);
                    args.Identity = identity;
                    return;
                }
            }

            // check for a user name token.
            UserNameIdentityToken userNameToken = args.NewIdentity as UserNameIdentityToken;

            if (userNameToken != null)
            {
                var identity = new UserIdentity(userNameToken);
                var token = (UserNameSecurityToken)identity.GetSecurityToken();
                var roles = new List<NodeId>();

                switch (token.UserName)
                {
                    case "gdsadmin":
                    {
                        if (token.Password != "demo")
                        {
                            throw ServiceResultException.Create(StatusCodes.BadIdentityTokenRejected, "Login failed for user: {0}", token.UserName);
                        }

                        break;
                    }

                    case "appadmin":
                    {
                        if (token.Password != "demo")
                        {
                            throw ServiceResultException.Create(StatusCodes.BadIdentityTokenRejected, "Login failed for user: {0}", token.UserName);
                        }

                        break;
                    }

                    case "appuser":
                    {
                        if (token.Password != "demo")
                        {
                            throw ServiceResultException.Create(StatusCodes.BadIdentityTokenRejected, "Login failed for user: {0}", token.UserName);
                        }

                        break;
                    }

                    default:
                    {
                        UserIdentity.VerifyPassword((UserNameSecurityToken)identity.GetSecurityToken());
                        break;
                    }
                }

                // valid token means the user has been authenticated.
                roles.Add(ObjectIds.WellKnownRole_AuthenticatedUser);

                IList<NodeId> rolesForUser = null;

                if (m_users.TryGetValue(token.UserName, out rolesForUser))
                {
                    foreach (var roleForUser in rolesForUser)
                    {
                        if (!roles.Contains(roleForUser))
                        {
                            roles.Add(roleForUser);
                        }
                    }
                }

                args.Identity = new RoleBasedIdentity(identity, roles);
                Utils.Trace("UserName Token Accepted: {0}", args.Identity.DisplayName);
                return;
            }

            // check for a anonymous token.
            AnonymousIdentityToken anonymousToken = args.NewIdentity as AnonymousIdentityToken;

            if (anonymousToken != null)
            {
                var identity = new UserIdentity(anonymousToken);
                args.Identity = new RoleBasedIdentity(identity, new NodeId[] { ObjectIds.WellKnownRole_Anonymous });
                Utils.Trace("Anonymous Token Accepted: {0}", args.Identity.DisplayName);
                return;
            }

            throw new ServiceResultException(StatusCodes.BadIdentityTokenRejected);
        }

        /// <summary>
        /// Initializes the validator from the configuration for a token policy.
        /// </summary>
        /// <param name="issuerCertificate">The issuer certificate.</param>
        private SecurityTokenResolver CreateSecurityTokenResolver(CertificateIdentifier issuerCertificate)
        {
            if (issuerCertificate == null)
            {
                throw new ArgumentNullException("issuerCertificate");
            }

            // find the certificate.
            X509Certificate2 certificate = issuerCertificate.Find(false);

            if (certificate == null)
            {
                throw ServiceResultException.Create(
                    StatusCodes.BadCertificateInvalid,
                    "Could not find issuer certificate: {0}",
                    issuerCertificate);
            }

            // create a security token representing the certificate.
            List<SecurityToken> tokens = new List<SecurityToken>();
            tokens.Add(new X509SecurityToken(certificate));

            // create issued token resolver.
            SecurityTokenResolver tokenResolver = SecurityTokenResolver.CreateDefaultSecurityTokenResolver(
                new System.Collections.ObjectModel.ReadOnlyCollection<SecurityToken>(tokens),
                false);

            return tokenResolver;
        }
        
        /// <summary>
        /// Verifies that a certificate user token is trusted.
        /// </summary>
        private void VerifyCertificate(X509Certificate2 certificate)
        {
            try
            {
                m_certificateValidator.Validate(certificate);
            }
            catch (Exception e)
            {
                // construct translation object with default text.
                TranslationInfo info = new TranslationInfo(
                    "InvalidCertificate",
                    "en-US",
                    "'{0}' is not a trusted user certificate.",
                    certificate.Subject);

                // create an exception with a vendor defined sub-code.
                throw new ServiceResultException(new ServiceResult(
                    e,
                    StatusCodes.BadIdentityTokenRejected,
                    "InvalidCertificate",
                    Opc.Ua.Gds.Namespaces.OpcUaGds,
                    new LocalizedText(info)));
            }
        }

        /// <summary>
        /// Validates a Kerberos WSS user token.
        /// </summary>
        private SecurityToken ParseAndVerifyKerberosToken(byte[] tokenData)
        {
            XmlDocument document = new XmlDocument();
            XmlNodeReader reader = null;

            try
            {
                document.InnerXml = new UTF8Encoding().GetString(tokenData).Trim();
                reader = new XmlNodeReader(document.DocumentElement);

                SecurityToken securityToken = new WSSecurityTokenSerializer().ReadToken(reader, null);
                System.IdentityModel.Tokens.KerberosReceiverSecurityToken receiver = securityToken as KerberosReceiverSecurityToken;

                KerberosSecurityTokenAuthenticator authenticator = new KerberosSecurityTokenAuthenticator();

                if (authenticator.CanValidateToken(receiver))
                {
                    authenticator.ValidateToken(receiver);
                }

                return securityToken;
            }
            catch (Exception e)
            {
                // construct translation object with default text.
                TranslationInfo info = new TranslationInfo(
                    "InvalidKerberosToken",
                    "en-US",
                    "'{0}' is not a valid Kerberos token.",
                    document.DocumentElement.LocalName);

                // create an exception with a vendor defined sub-code.
                throw new ServiceResultException(new ServiceResult(
                    e,
                    StatusCodes.BadIdentityTokenRejected,
                    "InvalidKerberosToken",
                    Opc.Ua.Gds.Namespaces.OpcUaGds,
                    new LocalizedText(info)));
            }
            finally
            {
                if (reader != null)
                {
                    reader.Close();
                }
            }
        }
        #endregion
    }
}
