namespace ndn_iot.bootstrap {
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.IO;
    //using Google.Protobuf;

    // For callbacks
    using System.Runtime.InteropServices;

    using net.named_data.jndn.security.policy;    
    using net.named_data.jndn;
    using net.named_data.jndn.encoding;
    using net.named_data.jndn.util;
    using net.named_data.jndn.security;
    using net.named_data.jndn.security.identity;
    using net.named_data.jndn.security.certificate;
    using net.named_data.jndn.encoding.tlv;

    // TODO: to be removed after inline testing's done
    using net.named_data.jndn.transport;

    // SimpleJSON for parsing controller response
    // included since dependency for our Unity application?
    using SimpleJSON;

    using ndn_iot.util;

    // TODO: abandoned protobuf-C# for now, not wise to investigate given we don't have enough time, hack controller instead
    //using ndn_iot.bootstrap.command;

    public delegate void OnRequestSuccess();
    public delegate void OnRequestFailed(string msg);
    
    public delegate void OnUpdateSuccess(string schema, bool isInitial);
    public delegate void OnUpdateFailed(string msg);

    public class Bootstrap : OnRegisterFailed {
        public Bootstrap(Face face) {
            //applicationName_ = "";

            string homePath = (Environment.OSVersion.Platform == PlatformID.Unix || Environment.OSVersion.Platform == PlatformID.MacOSX)
                                ? Environment.GetEnvironmentVariable("HOME")
                                : Environment.ExpandEnvironmentVariables("%HOMEDRIVE%%HOMEPATH%");
            keyPath_ = System.IO.Path.Combine(homePath, ".ndn/ndnsec-tpm-file/");
            string idStorgePath = System.IO.Path.Combine(homePath, ".ndn/ndnsec-public-info.db");

            identityManager_ = new IdentityManager(new BasicIdentityStorage(idStorgePath), new FilePrivateKeyStorage(keyPath_));
            certificateCache_ = new CertificateCache();
            policyManager_ = new ConfigPolicyManager("", certificateCache_);
            policyManager_.load(@"
              validator            
              {                    
                rule               
                {                  
                  id ""initial rule""
                  for data           
                  checker            
                  {                  
                    type hierarchical
                  }                
                }                  
              }", "initial-rule");

            keyChain_ = new KeyChain(identityManager_, policyManager_);
            
            face_ = face;
            keyChain_.setFace(face_);

            certificateContentCache_ = new MemoryContentCache(face_);
            trustSchemas_ = new Dictionary<string, AppTrustSchema>();
        }

        public KeyChain setupDefaultIdentityAndRoot(Name defaultIdentityName, Name signerName) {
            if (defaultIdentityName.size() == 0) {
                // Default identity does not exist
                try {
                    defaultIdentityName = identityManager_.getDefaultIdentity();
                } catch (SecurityException ex) {
                    throw new SystemException("Default identity does not exist: " + ex.Message);
                }
            }

            try {
                defaultIdentity_ = new Name(defaultIdentityName);
                defaultCertificateName_ = identityManager_.getDefaultCertificateNameForIdentity(defaultIdentity_);
                defaultKeyName_ = identityManager_.getDefaultKeyNameForIdentity(defaultIdentity_);
                if (defaultKeyName_.size() == 0) {
                    throw new SystemException("Cannot find a key name for identity: " + defaultIdentity_.toUri() + "\n");
                }
            } catch (SecurityException ex) {
                Console.Out.WriteLine(ex.Message);
                throw new SystemException("Security exception: " + ex.Message + " (default identity: " + defaultIdentity_.toUri() + ")");
            }

            IdentityCertificate myCertificate = keyChain_.getCertificate(defaultCertificateName_);
            Name actualSignerName = KeyLocator.getFromSignature(myCertificate.getSignature()).getKeyName();

            if (signerName.size() > 0 && !(actualSignerName.equals(signerName))) {
                throw new SystemException("Security exception: expected signer name does not match with actual signer name: " + signerName.toUri() + " " + actualSignerName.toUri());
            }
            controllerName_ = getIdentityNameFromCertName(actualSignerName);

            Console.Out.WriteLine("Controller name is: " + controllerName_.toUri());
            try {
                controllerCertificate_ = keyChain_.getCertificate(identityManager_.getDefaultCertificateNameForIdentity(controllerName_));
                certificateCache_.insertCertificate(controllerCertificate_);
            } catch (SecurityException ex) {
                throw new SystemException("Default certificate for controller identity does not exist: " + ex.Message);
            }

            face_.setCommandSigningInfo(keyChain_, defaultCertificateName_);
            certificateContentCache_.registerPrefix(new Name(defaultCertificateName_).getPrefix(-1), this);
            certificateContentCache_.add(myCertificate);

            return keyChain_;
        }

        public KeyChain getKeyChain() {
            return keyChain_;
        }

        /**
         * Publishing authorization
         */
        public void requestProducerAuthorization(Name dataPrefix, string applicationName, OnRequestSuccess onRequestSuccess, OnRequestFailed onRequestFailed) {
            if (defaultCertificateName_.size() == 0) {
                return;
            }
            sendAppRequest(defaultCertificateName_, dataPrefix, applicationName, onRequestSuccess, onRequestFailed);
        }

        public void sendAppRequest(Name certificateName, Name dataPrefix, string applicationName, OnRequestSuccess onRequestSuccess, OnRequestFailed onRequestFailed) {
            Blob encoding = AppRequestEncoder.encodeAppRequest(certificateName, dataPrefix, applicationName);
            //Console.Out.WriteLine("Encoding: " + encoding.toHex());
            Name requestInterestName = new Name(controllerName_);
            requestInterestName.append("requests").append(new Name.Component(encoding));

            Interest requestInterest = new Interest(requestInterestName);
            requestInterest.setInterestLifetimeMilliseconds(4000);
            // don't use keyChain.sign(interest), since it's of a different format from commandInterest
            face_.makeCommandInterest(requestInterest);

            AppRequestHandler appRequestHandler = new AppRequestHandler(this, onRequestSuccess, onRequestFailed);
            face_.expressInterest(requestInterest, appRequestHandler, appRequestHandler);
            
            return ;
        }

        public class AppRequestHandler : OnData, OnTimeout {
            public AppRequestHandler(Bootstrap bootstrap, OnRequestSuccess onRequestSuccess, OnRequestFailed onRequestFailed) {
                onRequestSuccess_ = onRequestSuccess;
                onRequestFailed_ = onRequestFailed;
                bootstrap_ = bootstrap;
                appRequestTimeoutCnt_ = 3;
            }

            public void onData(Interest interest, Data data) {
                AppRequestVerifyHandler verifyHandler = new AppRequestVerifyHandler(onRequestSuccess_, onRequestFailed_);
                bootstrap_.keyChain_.verifyData(data, verifyHandler, verifyHandler);
            }

            public void onTimeout(Interest interest) {
                Console.Out.WriteLine("Application publishing request times out");
                if (appRequestTimeoutCnt_ == 0) {
                    onRequestFailed_("Application publishing request times out");
                } else {
                    Interest newInterest = new Interest(interest);
                    bootstrap_.face_.expressInterest(newInterest, this, this);
                    appRequestTimeoutCnt_ -= 1;
                }
            }

            OnRequestSuccess onRequestSuccess_;
            OnRequestFailed onRequestFailed_;
            Bootstrap bootstrap_;
            int appRequestTimeoutCnt_;
        }

        public class AppRequestVerifyHandler: OnVerified, OnDataValidationFailed {
            public AppRequestVerifyHandler(OnRequestSuccess onRequestSuccess, OnRequestFailed onRequestFailed) {
                onRequestSuccess_ = onRequestSuccess;
                onRequestFailed_ = onRequestFailed;
            }

            public void onVerified(Data data) {
                string content = Util.dataContentToString(data);
                var response = JSON.Parse(content);
                string status = response["status"];
                if (status == "200") {
                    onRequestSuccess_();
                } else {
                    onRequestFailed_("Application request failed with message: " + content);
                }
            }

            public void onDataValidationFailed(Data data, string reason) {
                onRequestFailed_("Application request response verification failed: " + reason);
            }

            OnRequestSuccess onRequestSuccess_;
            OnRequestFailed onRequestFailed_;
        }

        /**
         * Handling application consumption (trust schema update)
         */
        public void startTrustSchemaUpdate(Name appPrefix, OnUpdateSuccess onUpdateSuccess, OnUpdateFailed onUpdateFailed) {
            string appNamespace = appPrefix.toUri();
            if (trustSchemas_.ContainsKey(appNamespace)) {
                if (trustSchemas_[appNamespace].getFollowing()) {
                    return;
                }
                trustSchemas_[appNamespace].setFollowing(true);
            } else {
                trustSchemas_[appNamespace] = new AppTrustSchema(true, "", 0, true);
            }

            Interest initialInterest = new Interest(new Name(appNamespace).append("_schema"));
            initialInterest.setChildSelector(1);

            TrustSchemaUpdateHandler trustSchemaUpdateHandler = new TrustSchemaUpdateHandler(onUpdateSuccess, onUpdateFailed, this);
            face_.expressInterest(initialInterest, trustSchemaUpdateHandler, trustSchemaUpdateHandler);
        }

        class TrustSchemaUpdateHandler : OnData, OnTimeout {
            public TrustSchemaUpdateHandler(OnUpdateSuccess onUpdateSuccess, OnUpdateFailed onUpdateFailed, Bootstrap bs) {
                onUpdateSuccess_ = onUpdateSuccess;
                onUpdateFailed_ = onUpdateFailed;
                bs_ = bs;
            }

            public void onData(Interest interest, Data data) {
                Console.Out.WriteLine("Received trust schema update data");
                TrustSchemaVerifyHandler trustSchemaVerifyHandler = new TrustSchemaVerifyHandler(onUpdateSuccess_, onUpdateFailed_, bs_);
                bs_.getKeyChain().verifyData(data, trustSchemaVerifyHandler, trustSchemaVerifyHandler);
            }

            public void onTimeout(Interest interest) {
                Console.Out.WriteLine("Trust schema update times out");
            }

            Bootstrap bs_;
            OnUpdateSuccess onUpdateSuccess_;
            OnUpdateFailed onUpdateFailed_;
        }

        class TrustSchemaVerifyHandler : OnVerified, OnDataValidationFailed {
            public TrustSchemaVerifyHandler(OnUpdateSuccess onUpdateSuccess, OnUpdateFailed onUpdateFailed, Bootstrap bs) {
                onUpdateSuccess_ = onUpdateSuccess;
                onUpdateFailed_ = onUpdateFailed;
                bs_ = bs;
            }

            public void onVerified(Data data) {
                var versionComponent = data.getName().get(-1);
                var version = versionComponent.toVersion();
                string appNamespace = data.getName().getPrefix(-2).toUri();
                var trustSchemas = bs_.trustSchemas_;
                if (trustSchemas.ContainsKey(appNamespace)) {
                    if (version < trustSchemas[appNamespace].getVersion()) {
                        Console.Out.WriteLine("Got out of date schema");
                        return;
                    }
                    trustSchemas[appNamespace].setVersion(version);
                    string schemaContent = Util.dataContentToString(data);
                    trustSchemas[appNamespace].setSchema(schemaContent);

                    Interest newInterest = new Interest(new Name(data.getName()).getPrefix(-1));
                    newInterest.setChildSelector(1);
                    Exclude exclude = new Exclude();
                    exclude.appendAny();
                    exclude.appendComponent(versionComponent);
                    newInterest.setExclude(exclude);

                    TrustSchemaUpdateHandler tsuh = new TrustSchemaUpdateHandler(onUpdateSuccess_, onUpdateFailed_, bs_);
                    bs_.face_.expressInterest(newInterest, tsuh, tsuh);
                    bs_.policyManager_.load(schemaContent, "updated-schema");
                    onUpdateSuccess_(schemaContent, trustSchemas[appNamespace].getIsInitial());
                    trustSchemas[appNamespace].setIsInitial(false);
                } else {
                    Console.Out.WriteLine("unexpected: received trust schema for application namespace that's not being followed; malformed data name?");
                    return;
                }
            }

            public void onDataValidationFailed(Data data, string reason) {
                onUpdateFailed_("Trust schema verification failed: " + reason);
            }

            OnUpdateSuccess onUpdateSuccess_;
            OnUpdateFailed onUpdateFailed_;
            Bootstrap bs_;
        }

        class AppTrustSchema {
            public AppTrustSchema(bool following, string schema, long version, bool isInitial) {
                following_ = following;
                schema_ = schema;
                version_ = version;
                isInitial_ = isInitial;
            }

            public bool getFollowing() {
                return following_;
            }

            public void setFollowing(bool following) {
                following_ = following;
            }

            public string getSchema() {
                return schema_;
            }

            public void setSchema(string schema) {
                schema_ = schema;
            }

            public long getVersion() {
                return version_;
            }

            public void setVersion(long version) {
                version_ = version;
            }

            public bool getIsInitial() {
                return isInitial_;
            }

            public void setIsInitial(bool isInitial) {
                isInitial_ = isInitial;
            }

            bool following_; 
            string schema_; 
            long version_; 
            bool isInitial_;
        }


        /**
         * Helper functions
         */

        // debug function: same extracted from ndn-dot-net library
        private string nameTransform(string keyName, string extension) {
            byte[] hash;
            try {
                hash = net.named_data.jndn.util.Common.digestSha256(ILOG.J2CsMapping.Util.StringUtil.GetBytes(keyName,"UTF-8"));
            } catch (IOException ex) {
                // We don't expect this to happen.
                throw new Exception("UTF-8 encoder not supported: " + ex.Message);
            }
            string digest = net.named_data.jndn.util.Common.base64Encode(hash);
            digest = digest.replace('/', '%');

            return digest + extension;
        }

        // get identity name from certificate name
        private Name getIdentityNameFromCertName(Name certName)
        {
            int i = certName.size() - 1;

            string idString = "KEY";
            while (i >= 0) {
                if (certName.get(i).toEscapedString() == idString)
                    break;
                i -= 1;
            }
              
            if (i < 0) {
                return new Name();
            }

            return certName.getPrefix(i);
        }

        // generate identity and certificate
        /*
        public void createIdentityAndCertificate(Name identityName) {
            Console.Out.WriteLine("Creating identity and certificate");
            Name certificateName = identityManager_.createIdentityAndCertificate(identityName, new RsaKeyParams());
            IdentityCertificate certificate = memoryIdentityStorage_.getCertificate(certificateName);
            Console.Out.WriteLine("Certificate name: " + certificateName.toUri());
            string certString = Convert.ToBase64String(certificate.wireEncode().getImmutableArray());
            Console.Out.WriteLine(certString);
        }
        */

        public void onRegisterFailed(Name prefix) {
            Console.Out.WriteLine("Registration failed for prefix: " + prefix.toUri());
        }

        public Name getDefaultCertificateName() {
            return defaultCertificateName_;
        }

        Name defaultIdentity_;
        Name defaultKeyName_;
        Name defaultCertificateName_;

        Name controllerName_;
        IdentityCertificate controllerCertificate_;

        //string applicationName_;
        IdentityManager identityManager_;
        ConfigPolicyManager policyManager_;
        CertificateCache certificateCache_;

        string keyPath_;

        public KeyChain keyChain_;
        public Face face_;
        MemoryContentCache certificateContentCache_;
        Dictionary<string, AppTrustSchema> trustSchemas_;
    }

    // TestEncodeAppRequest: credit to Jeff T
    class AppRequestEncoder {
        /// <summary>
        /// Encode the name as NDN-TLV to the encoder, using the given TLV type.
        /// </summary>
        ///
        /// <param name="name">The name to encode.</param>
        /// <param name="type">The TLV type</param>
        /// <param name="encoder">The TlvEncoder to receive the encoding.</param>
        private static void encodeName(Name name, int type, TlvEncoder encoder) 
        {
            int saveLength = encoder.getLength();

            // Encode the components backwards.
            for (int i = name.size() - 1; i >= 0; --i)
                encoder.writeBlobTlv(Tlv.NameComponent, name.get(i).getValue().buf());

            encoder.writeTypeAndLength(type, encoder.getLength() - saveLength);
        }

        /// <summary>
        /// Encode the value as NDN-TLV AppRequest, according to this Protobuf definition:
        /// </summary>
        ///
        /// <param name="idName">The idName.</param>
        /// <param name="dataPrefix">The dataPrefix</param>
        /// <param name="appName">The appName.</param>
        /// <returns>A Blob containing the encoding.</returns>
        static public Blob encodeAppRequest(Name idName, Name dataPrefix, string appName)
        {
            TlvEncoder encoder = new TlvEncoder();
            int saveLength = encoder.getLength();

            const int Tlv_idName = 220;
            const int Tlv_dataPrefix = 221;
            const int Tlv_appName = 222;
            const int Tlv_AppRequest = 223;

            // Encode backwards.
            encoder.writeBlobTlv(Tlv_appName, new Blob(appName).buf());
            encodeName(dataPrefix, Tlv_dataPrefix, encoder);
            encodeName(idName, Tlv_idName, encoder);

            encoder.writeTypeAndLength(Tlv_AppRequest, encoder.getLength() - saveLength);
            return new Blob(encoder.getOutput(), false);
        }
    }
}