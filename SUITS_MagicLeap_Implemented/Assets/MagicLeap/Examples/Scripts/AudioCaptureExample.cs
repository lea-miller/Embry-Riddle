// %BANNER_BEGIN%
// ---------------------------------------------------------------------
// %COPYRIGHT_BEGIN%
//
// Copyright (c) 2019-present, Magic Leap, Inc. All Rights Reserved.
// Use of this file is governed by the Developer Agreement, located
// here: https://auth.magicleap.com/terms/developer
//
// %COPYRIGHT_END%
// ---------------------------------------------------------------------
// %BANNER_END%

using System.Collections;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.MagicLeap;

namespace MagicLeap
{
    /// <summary>
    /// This class uses a controller to start/stop audio capture
    /// using the Unity Microphone class. The audio is then played
    /// through an audio source attached to the parrot in the scene.
    /// </summary>
    public class AudioCaptureExample : MonoBehaviour
    {
        public enum CaptureMode
        {
            Inactive = 0,
            Realtime,
            Delayed
        }

        [SerializeField, Tooltip("The reference to the MLControllerConnectionHandlerBehavior in the scene.")]
        private MLControllerConnectionHandlerBehavior _controllerConnectionHandler = null;

        [SerializeField, Tooltip("The reference to the place from camera script for the parrot.")]
        private PlaceFromCamera _placeFromCamera = null;

        [SerializeField, Tooltip("The reference to the privilege requester in the scene.")]
        private MLPrivilegeRequesterBehavior _privilegeRequester = null;

        [SerializeField, Tooltip("The audio source that should capture the microphone input.")]
        private AudioSource _inputAudioSource = null;

        [SerializeField, Tooltip("The audio source that should replay the captured audio.")]
        private AudioSource _playbackAudioSource = null;

        [SerializeField, Tooltip("The main transform of the parrot.")]
        private Transform _parrotTransform = null;

        [SerializeField, Tooltip("The animator used on the parrot.")]
        private Animator _parrotAnimator = null;

        [SerializeField, Tooltip("The text to display the recording status.")]
        private Text _statusLabel = null;

        [Space]
        [Header("Delayed Playback")]
        [SerializeField, Range(1, 2), Tooltip("The pitch used for delayed audio playback.")]
        private float _pitch = 1.5f;

        private bool _canCapture = false;
        private bool _isCapturing = false;
        private CaptureMode _captureMode = CaptureMode.Inactive;
        private string _deviceMicrophone = string.Empty;

        private float _audioMaxSample = 0;
        private float[] _audioSamples = new float[128];

        private int _numSyncIterations = 30;
        private int _numSamplesLatency = 1024;
        private bool _playbackStarted = false;

        private bool _isAudioDetected = false;
        private float _audioLastDetectionTime = 0;
        private float _audioDetectionStart = 0;
        private float _audioDetectionEnd = 0;

        private const int AUDIO_CLIP_LENGTH_SECONDS = 60;
        private const int AUDIO_CLIP_FREQUENCY_HERTZ = 48000;
        private const float AUDIO_SENSITVITY_DECIBEL = 0.00015f;
        private const float AUDIO_CLIP_TIMEOUT_SECONDS = 2;
        private const float AUDIO_CLIP_FALLOFF_SECONDS = 0.5f;
        private const float ROTATION_DAMPING = 100;

        private const int NUM_SYNC_ITERATIONS = 30;
        private const int NUM_SAMPLES_LATENCY = 1024;

        private Quaternion CameraDirection
        {
            get
            {
                Vector3 direction = Camera.main.transform.position - _parrotTransform.position;
                direction.y = 0;

                if (direction != Vector3.zero)
                {
                    return Quaternion.LookRotation(direction);
                }
                else
                {
                    return Quaternion.identity;
                }
            }
        }

        void Awake()
        {
            if (_inputAudioSource == null)
            {
                Debug.LogError("Error: AudioCaptureExample._inputAudioSource is not set, disabling script.");
                enabled = false;
                return;
            }

            if (_playbackAudioSource == null)
            {
                Debug.LogError("Error: AudioCaptureExample._playbackAudioSource is not set, disabling script.");
                enabled = false;
                return;
            }

            if (_parrotTransform == null)
            {
                Debug.LogError("Error: AudioCaptureExample._parrotTransform is not set, disabling script.");
                enabled = false;
                return;
            }

            if (_parrotAnimator == null)
            {
                Debug.LogError("Error: AudioCaptureExample._parrotAnimator is not set, disabling script.");
                enabled = false;
                return;
            }

            if (_statusLabel == null)
            {
                Debug.LogError("Error: AudioCaptureExample._statusLabel is not set, disabling script.");
                enabled = false;
                return;
            }

            if (_controllerConnectionHandler == null)
            {
                Debug.LogError("Error: AudioCaptureExample._controllerConnectionHandler is not set, disabling script.");
                enabled = false;
                return;
            }

            if (_placeFromCamera == null)
            {
                Debug.LogError("Error: AudioCaptureExample._placeFromCamera is not set, disabling script.");
                enabled = false;
                return;
            }

            UpdateStatus();

            #if PLATFORM_LUMIN
            // Before enabling the microphone, the scene must wait until the privileges have been granted.
            _privilegeRequester.OnPrivilegesDone += HandleOnPrivilegesDone;
            MLInput.OnControllerButtonDown += HandleOnButtonDown;
            MLInput.OnTriggerDown += HandleOnTriggerDown;
            #endif
        }

        void OnDestroy()
        {
            #if PLATFORM_LUMIN
            _privilegeRequester.OnPrivilegesDone -= HandleOnPrivilegesDone;
            MLInput.OnControllerButtonDown -= HandleOnButtonDown;
            MLInput.OnTriggerDown -= HandleOnTriggerDown;
            #endif

            StopCapture();
        }

        private void Update()
        {
            // Adjust the parrot to always face the camera.
            _parrotTransform.rotation = Quaternion.Slerp(
                _parrotTransform.rotation, CameraDirection, Time.deltaTime * ROTATION_DAMPING);

            if (_isCapturing)
            {
                ProcessAudioPlayback();
            }

            if (_playbackStarted)
            {
                if (_captureMode == CaptureMode.Delayed)
                {
                    DetectAudio();
                }

                AnimateParrot((_captureMode == CaptureMode.Realtime) ? _inputAudioSource : _playbackAudioSource);
            }

            UpdateStatus();
        }

        void OnApplicationPause(bool pause)
        {
            if (pause)
            {
                // require privledges to be checked again.
                _canCapture = false;
                _captureMode = CaptureMode.Inactive;

                if (_playbackStarted)
                {
                    StopCapture();
                }
            }
        }

        private void StartMicrophone()
        {
            if (_captureMode == CaptureMode.Inactive)
            {
                Debug.LogError("Error: AudioCaptureExample.StartMicrophone() cannot start with CaptureMode.Inactive.");
                return;
            }

            // Use the first detected Microphone device.
            if (Microphone.devices.Length > 0)
            {
                _deviceMicrophone = Microphone.devices[0];
            }

            // If no microphone is detected, exit early and log the error.
            if (string.IsNullOrEmpty(_deviceMicrophone))
            {
                Debug.LogError("Error: AudioCaptureExample._deviceMicrophone could not find a microphone device, disabling script.");
                enabled = false;
                return;
            }

            _playbackAudioSource.Stop();
            _inputAudioSource.loop = true;
            _inputAudioSource.clip = Microphone.Start(_deviceMicrophone, true, AUDIO_CLIP_LENGTH_SECONDS, AUDIO_CLIP_FREQUENCY_HERTZ);

            _isCapturing = true;
            _numSamplesLatency = NUM_SAMPLES_LATENCY;
            _numSyncIterations = NUM_SYNC_ITERATIONS;
        }

        private void ProcessAudioPlayback()
        {
            if (_numSyncIterations > 0)
            {
                --_numSyncIterations;
            }

            if (!_playbackStarted && (_numSyncIterations == 0) && (Microphone.GetPosition(_deviceMicrophone) > _numSamplesLatency))
            {
                _inputAudioSource.Play();
                _inputAudioSource.timeSamples = Microphone.GetPosition(_deviceMicrophone) - _numSamplesLatency;
                _playbackStarted = true;

                switch (_captureMode)
                {
                    case CaptureMode.Realtime:
                        {
                            _playbackAudioSource.pitch = 1;
                            _playbackAudioSource.clip = _inputAudioSource.clip;
                            _playbackAudioSource.loop = true;
                            _playbackAudioSource.Play();

                            break;
                        }

                    case CaptureMode.Delayed:
                        {
                            _playbackAudioSource.pitch = _pitch;
                            _playbackAudioSource.loop = false;
                            break;
                        }
                }
            }

            // Increasing latency
            if ((_inputAudioSource.timeSamples > Microphone.GetPosition(_deviceMicrophone)) && (Microphone.GetPosition(_deviceMicrophone) > _numSamplesLatency * 4))
            {
                _numSamplesLatency = _numSamplesLatency * 2;
                _inputAudioSource.timeSamples = Microphone.GetPosition(_deviceMicrophone) - _numSamplesLatency;
            }
        }

        private void StopCapture()
        {
            _isCapturing = false;
            _playbackStarted = false;

            // Stop microphone and input audio source.
            _inputAudioSource.Stop();

            if (!string.IsNullOrEmpty(_deviceMicrophone))
            {
                Microphone.End(_deviceMicrophone);
            }

            // Stop audio playback source and reset settings.
            _playbackAudioSource.Stop();
            _playbackAudioSource.loop = false;
            _playbackAudioSource.clip = null;
        }

        /// <summary>
        /// Update the example status label.
        /// </summary>
        private void UpdateStatus()
        {
            _statusLabel.text = string.Format("<color=#dbfb76><b>{0} {1}</b></color>\n{2}: {3}\n",
                LocalizeManager.GetString("Controller"),
                LocalizeManager.GetString("Data"),
                LocalizeManager.GetString("Status"),
                LocalizeManager.GetString(ControllerStatus.Text));

            _statusLabel.text += string.Format("\n<color=#dbfb76><b>{0} {1}</b></color>\n", LocalizeManager.GetString("AudioCapture"), LocalizeManager.GetString("Data"));
            if (!_canCapture)
            {
                _statusLabel.text += (_privilegeRequester.State != MLPrivilegeRequesterBehavior.PrivilegeState.Failed) ? string.Format("{0}: {1}", LocalizeManager.GetString("Status"), LocalizeManager.GetString("Requesting Privileges")) : string.Format("{0}: {1}", LocalizeManager.GetString("Status"), LocalizeManager.GetString("Privileges Denied"));
            }
            else
            {
                _statusLabel.text += string.Format("{0}: {1}", LocalizeManager.GetString("Status"), LocalizeManager.GetString(_captureMode.ToString()));
            }
        }

        private void DetectAudio()
        {
            // Analyze the input spectrum data, to determine when someone is speaking.
            _inputAudioSource.GetSpectrumData(_audioSamples, 0, FFTWindow.Rectangular);
            _audioMaxSample = _audioSamples.Max();

            if (_audioMaxSample > AUDIO_SENSITVITY_DECIBEL)
            {
                // Note the first moment speech was detected.
                _audioLastDetectionTime = Time.time;

                if (_isAudioDetected == false)
                {
                    _isAudioDetected = true;
                    _audioDetectionStart = _inputAudioSource.time;
                }
            }
            else if (_isAudioDetected && Time.time > _audioLastDetectionTime + AUDIO_CLIP_TIMEOUT_SECONDS)
            {
                // Note the last moment speach was detected.
                _audioDetectionEnd = _inputAudioSource.time - (AUDIO_CLIP_TIMEOUT_SECONDS - AUDIO_CLIP_FALLOFF_SECONDS);

                // Create the playback clip.
                _playbackAudioSource.clip = CreateAudioClip(_inputAudioSource.clip, _audioDetectionStart, _audioDetectionEnd);
                if (_playbackAudioSource.clip != null)
                {
                    _playbackAudioSource.Play();
                }

                // Reset and allow for new captured speech.
                _isAudioDetected = false;
                _audioDetectionStart = 0;
                _audioDetectionEnd = 0;
            }
        }

        private void AnimateParrot(AudioSource audioSource)
        {
            if (audioSource.isPlaying)
            {
                // Analyze the playback spectrum data to detect spikes in volume.
                audioSource.GetSpectrumData(_audioSamples, 0, FFTWindow.Rectangular);
                _audioMaxSample = _audioSamples.Max();

                // Toggle the talking animation.
                if (_audioMaxSample > AUDIO_SENSITVITY_DECIBEL)
                {
                    EnableTalkingAnimation(true, 1);
                }
                else
                {
                    EnableTalkingAnimation(false);
                }
            }
            else
            {
                EnableTalkingAnimation(false);
            }
        }

        /// <summary>
        /// Creates a new audio clip within the start and stop range.
        /// </summary>
        /// <param name="clip"></param>
        /// <param name="start"></param>
        /// <param name="stop"></param>
        /// <returns></returns>
        private AudioClip CreateAudioClip(AudioClip clip, float start, float stop)
        {
            int length = (int)(clip.frequency * (stop - start));
            if (length <= 0)
            {
                return null;
            }

            AudioClip audioClip = AudioClip.Create("Parrot_Voice", length, 1, clip.frequency, false);

            float[] data = new float[length];
            clip.GetData(data, (int)(clip.frequency * start));
            audioClip.SetData(data, 0);

            return audioClip;
        }

        private void EnableTalkingAnimation(bool enabled, float rate = 1)
        {
            // Set the properties for the parrot animator.
            _parrotAnimator.SetBool("IsTalking", enabled);
            _parrotAnimator.SetFloat("TalkingRate", rate);
        }

        /// <summary>
        /// Responds to privilege requester result.
        /// </summary>
        /// <param name="result"/>
        private void HandleOnPrivilegesDone(MLResult result)
        {
            #if PLATFORM_LUMIN
            if (!result.IsOk)
            {
                Debug.LogErrorFormat("Error: AudioCaptureExample failed to get all requested privileges, disabling script. Reason: {0}", result);
                UpdateStatus();
                enabled = false;
                return;
            }
            #endif

            _canCapture = true;
            Debug.Log("Succeeded in requesting all privileges");

        }

        private void HandleOnTriggerDown(byte controllerId, float triggerValue)
        {
            if (_canCapture)
            {
                _captureMode = (_captureMode == CaptureMode.Delayed) ? CaptureMode.Inactive : _captureMode + 1;

                // Stop & Start to clear the previous mode.
                if (_isCapturing)
                {
                    StopCapture();
                }

                if (_captureMode != CaptureMode.Inactive)
                {
                    StartMicrophone();
                }
            }
        }

        private void HandleOnButtonDown(byte controllerId, MLInput.Controller.Button button)
        {
            if(button == MLInput.Controller.Button.Bumper)
            {
                StartCoroutine("SingleFrameUpdate");
            }
        }

        private IEnumerator SingleFrameUpdate()
        {
            _placeFromCamera.PlaceOnUpdate = true;
            yield return new WaitForEndOfFrame();
            _placeFromCamera.PlaceOnUpdate = false;
        }
    }
}
