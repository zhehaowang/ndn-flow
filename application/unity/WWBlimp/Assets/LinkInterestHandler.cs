﻿using System;
using System.Collections.Generic;
using System.Text;
using ILOG.J2CsMapping.NIO;

using net.named_data.jndn;
using net.named_data.jndn.util;
using net.named_data.jndn.transport;
using net.named_data.jndn.security;
using net.named_data.jndn.security.identity;
using net.named_data.jndn.security.policy;

using ndn_iot.bootstrap;

class LinkInterestHandler : OnInterestCallback, OnRegisterFailed {
	WebComm webComm;

	public LinkInterestHandler(WebComm webComm) {
		this.webComm = webComm;
	}

	public void onInterest(Name prefix, Interest interest, Face face, long interestFilterId,
		InterestFilter filter)
	{
		webComm.gotALink ("GOT A LINK");
		int linkPrefixSize = webComm.getLinkPrefix ().size ();
		string phoneId = interest.getName().get(linkPrefixSize).toEscapedString();
		string linkContent = interest.getName().get(linkPrefixSize + 1).toEscapedString();

		webComm.gotALink("User " + phoneId + " clicked link \"" + linkContent + "\"");

		var data = new Data(interest.getName());
		var content = "User " + phoneId + " clicked link \"" + linkContent + "\"";
		data.setContent(new Blob(content));
		data.getMetaInfo().setFreshnessPeriod(2000);

		try {
			FaceSingleton.getKeychain().sign(data, FaceSingleton.getCertificateName());      
		} catch (SecurityException exception) {
			// Don't expect this to happen.
			throw new SecurityException("SecurityException in sign: " + exception);
		}

		webComm.gotALink("Sent content " + content);
		try {
			FaceSingleton.getFace().putData(data);
		} catch (Exception ex) {
			webComm.gotALink("Echo: Exception in sending data " + ex);
		}
	}

	public void onRegisterFailed(Name prefix) {
		webComm.gotALink("Register failed for prefix: " + prefix.toUri());
	}

}