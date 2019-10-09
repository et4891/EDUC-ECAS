﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Crm.Sdk.Messages;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Ecas.Dyn365.CASIntegration.Plugin;
using BCGov.Dyn365.CASIntegration.Plugin.Payment;

namespace Ecas.Dyn365.CASIntegration.Plugin
{
    public class SendToCAS : IPlugin
    {
        public void Execute(IServiceProvider serviceProvider)
        {
            IPluginExecutionContext context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            IOrganizationServiceFactory serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            IOrganizationService service = serviceFactory.CreateOrganizationService(context.UserId);
            ITracingService traceService = (ITracingService)serviceProvider.GetService(typeof(ITracingService));

            traceService.Trace("Initializing plugin..");

            if (context.Depth > 1)
                return;

            Entity targetEntity = null;
            if (context.Depth == 1 && context.InputParameters != null && context.InputParameters.Contains("Target") 
                && context.InputParameters["Target"] is Entity && context.PostEntityImages != null 
                && context.PostEntityImages.Count > 0)
            {
                targetEntity = context.InputParameters["Target"] as Entity;
                if (!targetEntity.Contains("statuscode"))
                    return;
                if (targetEntity.Contains("statuscode") && ((OptionSetValue)targetEntity["statuscode"]).Value != 610410001) //Ready for Processing
                    return;
            }
            traceService.Trace("Loaded Target Entity");

            bool isError = false;
            string userMessage = string.Empty;
            HttpClient httpClient = null;
            try
            {
                targetEntity = context.InputParameters["Target"] as Entity;
                if (!targetEntity.Contains("statuscode"))
                    return;
                if (targetEntity.Contains("statuscode") && ((OptionSetValue)targetEntity["statuscode"]).Value != 610410001) //Ready for Processing
                    return;

                traceService.Trace("Payment is Ready for Processing");

                var postImageEntity = context.PostEntityImages["PostImage"] as Entity;
                if (!postImageEntity.Contains("educ_payee"))
                    throw new InvalidPluginExecutionException("Payee lookup is empty on the payment..");
                if (!postImageEntity.Contains("educ_amount"))
                    throw new InvalidPluginExecutionException("Amount is empty on the payment");

                SetState(service, targetEntity.ToEntityReference(), 0, 610410000); //Processing

                traceService.Trace("Payment Set as Processing");

                var ecasServiceAccountUserId = new Guid(Helpers.GetSystemConfigurations(service, "Default", "ECASServiceAccountUserId")[0]["educ_value"].ToString());
                IOrganizationService adminService = serviceFactory.CreateOrganizationService(ecasServiceAccountUserId);
                var configs = Helpers.GetSystemConfigurations(adminService, "CAS-AP", string.Empty);
                
                string clientKey = Helpers.GetConfigKeyValue(configs, "ClientKey", "CAS-AP");
                string clientId = Helpers.GetConfigKeyValue(configs, "ClientId", "CAS-AP");
                string url = Helpers.GetConfigKeyValue(configs, "InterfaceUrl", "CAS-AP");

                traceService.Trace("Loaded Settings");

                var jsonRequest = GenerateInvoice(service, traceService, configs, postImageEntity).ToJSONString();

                httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Add("clientID", clientId);
                httpClient.DefaultRequestHeaders.Add("secret", clientKey);
                httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                httpClient.BaseAddress = new Uri(url);
                httpClient.Timeout = new TimeSpan(1, 0, 0);  // 1 hour timeout 

                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/api/CASAPTransaction");
                request.Content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

                traceService.Trace("-----JSON Payload Begin-----");
                traceService.Trace(jsonRequest);
                traceService.Trace("-----JSON Payload End-----");

                traceService.Trace("-----REQUEST INFO------");
                traceService.Trace("URL:" + url);
                traceService.Trace("ClientId:" + clientId);
                traceService.Trace("ClientKey:" + clientKey);


                HttpResponseMessage response = httpClient.SendAsync(request).Result;

                traceService.Trace("Invoked CAS AP Service");

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    userMessage = response.Content.ReadAsStringAsync().Result;
                    if(!userMessage.Contains("SUCCEEDED"))
                    {
                        throw new InvalidPluginExecutionException(userMessage);
                    }
                }
                else
                    throw new InvalidPluginExecutionException(response.StatusCode.ToString() + "\r\n" + jsonRequest); // throw new InvalidPluginExecutionException(string.Format("Error from CAS Interface:\r\nStatus='{0}'\r\nReason='{1}'\r\nRequest={2}", response.StatusCode, response.ReasonPhrase, response.RequestMessage.Content.ReadAsStringAsync().Result));
            }
            catch (InvalidPluginExecutionException ex)
            {
                traceService.Trace("Exception: " + ex.Message + " " + ex.StackTrace);
                isError = true;
                userMessage = ex.Message;
            }
            catch (FaultException<OrganizationServiceFault> ex)
            {
                traceService.Trace("Exception: " + ex.Message + " " + ex.StackTrace);
                isError = true;
                userMessage = ex.Message;
            }
            catch (Exception ex)
            {
                traceService.Trace("Exception: " + ex.Message + " " + ex.StackTrace);
                isError = true;
                userMessage = ex.Message;
            }
            finally
            {
                Entity updatePayment = new Entity("educ_payment");
                updatePayment.Id = ((Entity)context.InputParameters["Target"]).Id;
                
                if (!string.IsNullOrEmpty(userMessage))
                    updatePayment["educ_casresponse"] = userMessage;

                if (isError)
                    updatePayment["statuscode"] = new OptionSetValue(610410004); //Failed when attempting to send to CAS
                else
                    updatePayment["statuscode"] = new OptionSetValue(610410006); //Sent

                service.Update(updatePayment);

                if (httpClient != null)
                    httpClient.Dispose();
            }
        }

        private void SetState(IOrganizationService service, EntityReference target, int stateCode, int statusCode)
        {
            SetStateRequest req = new SetStateRequest();
            req.EntityMoniker = target;
            req.State = new OptionSetValue(stateCode);
            req.Status = new OptionSetValue(statusCode);

            service.Execute(req);
        }

        private Invoice GenerateInvoice(IOrganizationService service, ITracingService traceService, 
        List<Entity> configs, Entity paymentEntity)
        {
            traceService.Trace("Generating Invoice");

            #region Payee Details
            string supplierNumber = string.Empty;
            int siteNumber = int.MinValue;
            string firstName = string.Empty;
            string lastName = string.Empty;
            string addressLine1 = string.Empty;
            string addressLine2 = string.Empty;
            string addressLine3 = string.Empty;
            string city = string.Empty;
            string province = string.Empty;
            string country = string.Empty;
            string postalCode = string.Empty;

            EntityReference payeeLookup = paymentEntity["educ_payee"] as EntityReference;

            if (payeeLookup.LogicalName.Equals("account", StringComparison.InvariantCultureIgnoreCase))
            {
                var accountEntity = service.Retrieve(payeeLookup.LogicalName.ToLowerInvariant(), payeeLookup.Id,
                    new ColumnSet("educ_suppliernumber", "educ_suppliersitenumber", "name", "address1_line1", "address1_line2", "address1_line3",
                    "address1_city", "address1_stateorprovince", "address1_country", "address1_postalcode"));

                if (!accountEntity.Contains("educ_suppliernumber"))
                    throw new InvalidPluginExecutionException("Vendor Number on account is empty..");
                if (!accountEntity.Contains("educ_suppliersitenumber"))
                    throw new InvalidPluginExecutionException("Supplier Site Number on account is empty..");
                if (!accountEntity.Contains("name"))
                    throw new InvalidPluginExecutionException("Account Name is empty..");

                supplierNumber = (string)accountEntity["educ_suppliernumber"];
                siteNumber = Convert.ToInt32(accountEntity.GetAttributeValue<string>("educ_suppliersitenumber"));
                firstName = (string)accountEntity["name"];

                if (accountEntity.Contains("address1_line1"))
                    addressLine1 = (string)accountEntity["address1_line1"];
                if (accountEntity.Contains("address1_line2"))
                    addressLine1 = (string)accountEntity["address1_line2"];
                if (accountEntity.Contains("address1_line3"))
                    addressLine1 = (string)accountEntity["address1_line3"];
                if (accountEntity.Contains("address1_city"))
                    city = (string)accountEntity["address1_city"];
                if (accountEntity.Contains("address1_stateorprovince"))
                    province = (string)accountEntity["address1_stateorprovince"];
                if (accountEntity.Contains("address1_country"))
                    country = (string)accountEntity["address1_country"];
                if (accountEntity.Contains("address1_postalcode"))
                    postalCode = (string)accountEntity["address1_postalcode"];
            }
            else
            {
                var contactEntity = service.Retrieve(payeeLookup.LogicalName.ToLowerInvariant(), payeeLookup.Id,
                    new ColumnSet("educ_suppliernumber", "educ_suppliersitenumber", "firstname", "lastname", "address3_line1", "address3_line2", "address3_line3",
                    "address3_city", "address3_stateorprovince", "address3_country", "address3_postalcode"));

                if (!contactEntity.Contains("educ_suppliernumber"))
                    throw new InvalidPluginExecutionException("Supplier Number on contact is empty..");
                if (!contactEntity.Contains("educ_suppliersitenumber"))
                    throw new InvalidPluginExecutionException("Supplier Site Number on contact is empty..");
                if (!contactEntity.Contains("firstname"))
                    throw new InvalidPluginExecutionException("First Name on contact is empty..");
                if (!contactEntity.Contains("lastname"))
                    throw new InvalidPluginExecutionException("Last Name on contact is empty..");

                supplierNumber = (string)contactEntity["educ_suppliernumber"];
                siteNumber = Convert.ToInt32(contactEntity.GetAttributeValue<string>("educ_suppliersitenumber"));
                firstName = (string)contactEntity["firstname"];
                lastName = (string)contactEntity["lastname"];

                if (contactEntity.Contains("address1_line1"))
                    addressLine1 = (string)contactEntity["address1_line1"];
                if (contactEntity.Contains("address1_line2"))
                    addressLine2 = (string)contactEntity["address1_line2"];
                if (contactEntity.Contains("address1_line3"))
                    addressLine3 = (string)contactEntity["address1_line3"];
                if (contactEntity.Contains("address1_city"))
                    city = (string)contactEntity["address1_city"];
                if (contactEntity.Contains("address1_stateorprovince"))
                    province = (string)contactEntity["address1_stateorprovince"];
                if (contactEntity.Contains("address1_country"))
                    country = (string)contactEntity["address1_country"];
                if (contactEntity.Contains("address1_postalcode"))
                    postalCode = (string)contactEntity["address1_postalcode"];
            }

            traceService.Trace("Loaded Payee Information");
            #endregion

            #region AssignmentDetails & Session
            bool specialhandling = false;

            EntityReference assignmentLookup = paymentEntity["educ_assignment"] as EntityReference;
            var assignmentEntity = service.Retrieve(assignmentLookup.LogicalName.ToLowerInvariant(), assignmentLookup.Id,
                new ColumnSet("educ_dcheque"));

            if (assignmentEntity.Contains("educ_dcheque"))
                specialhandling = assignmentEntity.GetAttributeValue<bool>("educ_dcheque");

            EntityReference sessionLookup = paymentEntity["educ_session"] as EntityReference;
            var sessionEntity = service.Retrieve(sessionLookup.LogicalName.ToLowerInvariant(), sessionLookup.Id,
                new ColumnSet("educ_name"));

            string sessionShortName = string.Empty;
            if (sessionEntity.Contains("educ_name"))
            {
                sessionShortName = sessionEntity.GetAttributeValue<string>("educ_name");
                sessionShortName = sessionShortName.Length > 40 ? sessionShortName.Substring(1, 40) : sessionShortName;
            }

            traceService.Trace("Loaded Assignment and Session Information");
            #endregion



            #region Invoice Details
            DateTime? invoiceDate = DateTime.MinValue;
       
            //TODO: ETL on Method of payment. 
            string methodOfPayment = "GEN CHQ";
            //methodOfPayment = "GEN EFT";

            //if (!paymentEntity.Contains("educ_paymentnumber"))
            //    throw new InvalidPluginExecutionException("Payment Number is empty..");

            invoiceDate = DateTime.Now;
            //invoiceNumber = (string)paymentEntity["educ_paymentautonumber"];

            #endregion

            #region Mandatory Field Validations
            if (string.IsNullOrEmpty(supplierNumber))
                throw new InvalidPluginExecutionException("Supplier Number is empty..");
            if (siteNumber == int.MinValue)
                throw new InvalidPluginExecutionException("Supplier Site Number is empty..");
            if (!paymentEntity.Contains("educ_amount"))
                throw new InvalidPluginExecutionException("Invoice Amount is empty..");
            //if (!invoiceDate.HasValue)
            //    throw new InvalidPluginExecutionException("Invoice Date is empty..");
            #endregion

            var invoiceNumber = string.Format("ED-{0}", DateTime.Today.ToShortDateString().Replace("/", "-"));

            Invoice result = new Invoice()
            {
                //Mandatory values
                InvoiceType = Helpers.GetConfigKeyValue(configs, "InvoiceType", "CAS-AP"),
                SupplierNumber = supplierNumber,
                SupplierSiteNumber = Convert.ToInt32(siteNumber),
                InvoiceDate = invoiceDate.Value,
                InvoiceNumber = invoiceNumber,
                InvoiceAmount = ((Money)paymentEntity["educ_amount"]).Value,
                PayGroup = methodOfPayment,                
                DateInvoiceReceived = invoiceDate.Value,
                RemittanceCode = Helpers.GetConfigKeyValue(configs, "RemittanceCode", "CAS-AP"),
                SpecialHandling = specialhandling,
                Terms = Helpers.GetConfigKeyValue(configs, "Terms", "CAS-AP"),
                PayAloneFlag = "N",
                GLDate = invoiceDate,
                InvoiceBatchName = Helpers.GetConfigKeyValue(configs, "BatchName", "CAS-AP"),

                //Optional Value
                QualifiedReceiver = ((EntityReference)paymentEntity["ownerid"]).Name,
                //TODO: First 40 char charcters of session name
                PaymentAdviceComments = sessionShortName,
                CurrencyCode = Helpers.GetConfigKeyValue(configs, "CurrencyCode", "CAS-AP"),

                //Invoice Line Details
                InvoiceLineNumber = 1,
                InvoiceLineType = "Item",
                //TODO
                LineCode = Helpers.GetConfigKeyValue(configs, "LineCode", "CAS-AP"),
                InvoiceLineAmount = ((Money)paymentEntity["educ_amount"]).Value,
                DefaultDistributionAccount = ((OptionSetValue)paymentEntity["educ_paymenttype"]).Value == 610410000 ? 
                    Helpers.GetConfigKeyValue(configs, "FeeDistributionAccount", "CAS-AP") : 
                    Helpers.GetConfigKeyValue(configs, "ExpensesDistributionAccount", "CAS-AP")
            };

            traceService.Trace("Invoice Envelope Loaded");

            //if (((OptionSetValue)paymentEntity["educ_specialhandling"]).Value == 100000001 || 
            //    supplierNumber.Equals(Helpers.GetConfigKeyValue(configs, "BlockNumber", "CAS-AP"), 
            //    StringComparison.InvariantCultureIgnoreCase)) //DBack or Block
            //{
            //    if (!string.IsNullOrEmpty(firstName))
            //        result.NameLine1 = firstName;
            //    if (!string.IsNullOrEmpty(lastName))
            //        result.NameLine2 = lastName;
            //    if (!string.IsNullOrEmpty(addressLine1))
            //        result.AddressLine1 = addressLine1;
            //    if (!string.IsNullOrEmpty(addressLine2))
            //        result.AddressLine2 = addressLine2;
            //    if (!string.IsNullOrEmpty(addressLine3))
            //        result.AddressLine3 = addressLine3;
            //    if (!string.IsNullOrEmpty(city))
            //        result.City = city;
            //    if (!string.IsNullOrEmpty(country))
            //        result.Country = country;
            //    if (!string.IsNullOrEmpty(province))
            //        result.Province = province;
            //    if (!string.IsNullOrEmpty(postalCode))
            //        result.PostalCode = postalCode;

            //    result.PayAloneFlag = "Y";
            //}

            return result;
        }
    }
}