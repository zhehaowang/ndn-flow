import sys
import logging

from pyndn import Name, Data, Interest, Face
from pyndn.security import KeyChain

from ndn_iot_python.consumer.app_consumer import AppConsumer

# TODO: test case and example for this code

class AppConsumerSequenceNumber(AppConsumer):
    def __init__(self, face, keyChain, certificateName, doVerify, defaultPipelineSize = 5, startingSeqNumber = 0):
        super(AppConsumerSequenceNumber, self).__init__(face, keyChain, certificateName, doVerify)

        self._pipelineSize = defaultPipelineSize
        self._emptySlot = defaultPipelineSize
        self._currentSeqNumber = startingSeqNumber

        self._verifyFailedRetransInterval = 4000
        self._defaultInterestLifetime = 4000

        return

    """
    public interface
    """
    def consume(self, prefix, onVerified, onVerificationFailed, onTimeout):
        num = self._emptySlot
        for i in range(0, num):
            name = Name(prefix).append(str(self._currentSeqNumber))
            interest = Interest(name)
            # interest configuration / template?
            interest.setInterestLifetimeMilliseconds(self._defaultInterestLifetime)
            self._face.expressInterest(interest, 
              lambda i, d : self.onData(i, d, onVerified, onVerificationFailed, onTimeout), 
              lambda i: self.beforeReplyTimeout(i, onVerified, onVerificationFailed, onTimeout))
            self._currentSeqNumber += 1
            self._emptySlot -= 1
        return

    """
    internal functions
    """
    def onData(self, interest, data, onVerified, onVerificationFailed, onTimeout):
        if self._doVerify:
            self._keyChain.verifyData(data, 
              lambda d : self.beforeReplyDataVerified(d, onVerified, onVerificationFailed, onTimeout), 
              lambda d : self.beforeReplyVerificationFailed(d, interest, onVerified, onVerificationFailed, onTimeout))
        else:
            self.beforeReplyDataVerified(data, onVerified, onVerificationFailed, onTimeout)
        return

    def beforeReplyDataVerified(self, data, onVerified, onVerificationFailed, onTimeout):
        # fill the pipeline
        self._currentSeqNumber += 1
        self._emptySlot += 1
        self.consume(data.getName().getPrefix(-1), onVerified, onVerificationFailed, onTimeout)
        onVerified(data)
        return

    def beforeReplyVerificationFailed(self, data, interest, onVerified, onVerificationFailed, onTimeout):
        # for now internal to the library: verification failed cause the library to retransmit the interest after some time
        newInterest = Interest(interest)
        newInterest.refreshNonce()

        dummyInterest = Interest(Name("/local/timeout"))
        dummyInterest.setInterestLifetimeMilliseconds(self._verifyFailedRetransInterval)
        self.expressInterest(dummyInterest, 
          self.onDummyData, 
          lambda i: self.retransmitInterest(newInterest, onVerified, onVerificationFailed, onTimeout))
        onVerificationFailed(data)
        return

    def beforeReplyTimeout(self, interest, onVerified, onVerificationFailed, onTimeout):
        newInterest = Interest(interest)
        newInterest.refreshNonce()
        self.expressInterest(newInterest, 
          lambda i, d : self.onData(i, d, onVerified, onVerificationFailed, onTimeout), 
          lambda i: self.beforeReplyTimeout(i, onVerified, onVerificationFailed, onTimeout))
        onTimeout(interest)
        return

    def retransmitInterest(self, interest, onVerified, onVerificationFailed, onTimeout):
        self._face.expressInterest(interest, 
          lambda i, d : self.onData(i, d, onVerified, onVerificationFailed, onTimeout), 
          lambda i: self.beforeReplyTimeout(i, onVerified, onVerificationFailed, onTimeout))

    def onDummyData(self, interest, data):
        print "Unexpected: got dummy data!"
        return