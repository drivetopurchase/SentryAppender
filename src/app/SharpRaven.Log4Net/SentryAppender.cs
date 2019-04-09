using log4net.Appender;
using log4net.Core;
using log4net.Layout;
using log4net.Util;
using SharpRaven.Data;
using SharpRaven.Log4Net.Extra;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Web;

namespace SharpRaven.Log4Net
{
    public class SentryTag
    {
        public string Name { get; set; }
        public IRawLayout Layout { get; set; }
    }

    public class SentryAppender : AppenderSkeleton
    {
        protected IRavenClient RavenClient;
        public string DSN { get; set; }
        public string Logger { get; set; }
        public string Environment { get; set; }
        public string Release { get; set; }
        public string Tags { get; set; }
        private readonly List<SentryTag> tagLayouts = new List<SentryTag>();

        public void AddTag(SentryTag tag)
        {
            tagLayouts.Add(tag);
        }

        protected override void Append(LoggingEvent loggingEvent)
        {
            if (RavenClient == null)
            {
                RavenClient = new RavenClient(DSN)
                {
                    Logger = Logger,
                    Environment = Environment,
                    Release = Release,

                    // If something goes wrong when sending the event to Sentry, make sure this is written to log4net's internal
                    // log. See <add key="log4net.Internal.Debug" value="true"/>
                    ErrorOnCapture = ex => LogLog.Error(typeof(SentryAppender), "[" + Name + "] " + ex.Message, ex)
                };

                if (!string.IsNullOrWhiteSpace(Tags))
                {
                    string[] tags = Tags.Split('&');
                    foreach (string tagPair in tags)
                    {
                        string[] keyValue = tagPair.Split(new[] { '=' }, 2);
                        if (keyValue.Length == 2)
                        {
                            Layout2RawLayoutAdapter layout = new Layout2RawLayoutAdapter(new PatternLayout(HttpUtility.UrlDecode(keyValue[1])));
                            AddTag(new SentryTag { Name = keyValue[0], Layout = layout });
                        }
                    }
                }
            }

            SentryEvent sentryEvent = null;

            if (loggingEvent.ExceptionObject != null)
            {
                // We should capture both the exception and the message passed
                sentryEvent = new SentryEvent(loggingEvent.ExceptionObject)
                {
                    Message = loggingEvent.RenderedMessage
                };
            }
            else if (loggingEvent.MessageObject is Exception)
            {
                // We should capture the exception with no custom message
                sentryEvent = new SentryEvent(loggingEvent.MessageObject as Exception);
            }
            else
            {
                // Just capture message
                sentryEvent = new SentryEvent(loggingEvent.RenderedMessage);
            }

            // Assign error level
            sentryEvent.Level = Translate(loggingEvent.Level);

            // Format and add tags
            tagLayouts.ForEach(tl => sentryEvent.Tags.Add(tl.Name, (tl.Layout.Format(loggingEvent) ?? string.Empty).ToString()));

            // Add extra data
            sentryEvent.Extra = GetLoggingEventProperties(loggingEvent);

            RavenClient.Capture(sentryEvent);
        }

        public static ErrorLevel Translate(Level level)
        {
            switch (level.DisplayName)
            {
                case "WARN":
                    return ErrorLevel.Warning;

                case "NOTICE":
                    return ErrorLevel.Info;
            }

            return !Enum.TryParse(level.DisplayName, true, out ErrorLevel errorLevel)
                       ? ErrorLevel.Error
                       : errorLevel;
        }

        private IEnumerable<KeyValuePair<string, object>> GetLoggingEventProperties(LoggingEvent loggingEvent)
        {
            var properties = loggingEvent.GetProperties();
            if (properties == null)
            {
                yield break;
            }

            foreach (var key in properties.GetKeys())
            {
                if (!string.IsNullOrWhiteSpace(key)
                    && !key.StartsWith("log4net:", StringComparison.OrdinalIgnoreCase))
                {
                    var value = properties[key];
                    if (value != null
                        && (!(value is string stringValue) || !string.IsNullOrWhiteSpace(stringValue)))
                    {
                        yield return new KeyValuePair<string, object>(key, value);
                    }
                }
            }

            // Add extra data with or without HTTP-related fields
            HttpExtra httpExtra = HttpExtra.GetHttpExtra();
            if (httpExtra != null)
            {
                yield return new KeyValuePair<string, object>("http-extra", httpExtra);
            }
            if (Environment != null)
            {
                yield return new KeyValuePair<string, object>("env-extra", Environment);
            }
        }

        protected override void Append(LoggingEvent[] loggingEvents)
        {
            foreach (LoggingEvent loggingEvent in loggingEvents)
            {
                Append(loggingEvent);
            }
        }
    }
}
