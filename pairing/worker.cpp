// Native Windows Connect Now worker. Own implementation against the published SDK.
// Sequence reference: Microsoft Windows-classic-samples/Win7Samples/netds/WindowsConnectNow.
// Private pipe protocol: the managed owner redacts discovery addresses before UI logging.
// Pair mode rechecks the explicitly selected model, manufacturer and physical MAC.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <initguid.h>
#include <functiondiscovery.h>
#include <functiondiscoveryapi.h>
#include <functiondiscoverycategories.h>
#include <functiondiscoveryconstraints.h>
#include <functiondiscoverynotification.h>
#include <functiondiscoverykeys.h>
#include <WcnTypes.h>
#include <WcnDevice.h>
#include <WcnFunctionDiscoveryKeys.h>
#include <propsys.h>
#include <propvarutil.h>
#include <bcrypt.h>
#include <wrl/client.h>
#include <msxml6.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cstdio>
#include <cwctype>
#include <mutex>
#include <set>
#include <string>
#include <thread>
#include <iostream>
using Microsoft::WRL::ComPtr;
static constexpr int DiscoverySeconds = 90;
static constexpr int ProvisioningSeconds = 180;
static std::mutex outputMutex;
static void line(const std::string& s) noexcept { std::lock_guard<std::mutex> lock(outputMutex); std::puts(s.c_str()); std::fflush(stdout); }
static std::string jsonBool(bool v) { return v ? "true" : "false"; }
static std::string hash(const std::wstring& value) {
    unsigned char bytes[32]{};
    if (BCryptHash(BCRYPT_SHA256_ALG_HANDLE, nullptr, 0, reinterpret_cast<PUCHAR>(const_cast<wchar_t*>(value.data())),
                   static_cast<ULONG>(value.size() * sizeof(wchar_t)), bytes, sizeof bytes) < 0) throw 1;
    char out[65]{}; for (size_t i=0; i<32; ++i) sprintf_s(out+i*2, 3, "%02x", bytes[i]); return out;
}
static std::wstring normalized(std::wstring value) {
    while (!value.empty() && iswspace(value.back())) value.pop_back();
    size_t start=0; while (start<value.size() && iswspace(value[start])) ++start; value.erase(0,start);
    std::transform(value.begin(),value.end(),value.begin(),[](wchar_t c){ return static_cast<wchar_t>(towlower(c)); }); return value;
}
static std::string classify(const std::wstring& raw) {
    auto n=normalized(raw);
    if (n==L"pandev") return "pandev";
    if (n==L"nvidiablake") return "nvidia_blake";
    for (auto prefix : {L"nvidia",L"shield"}) {
        std::wstring p(prefix); if (n==p || (n.size()>p.size() && n.compare(0,p.size(),p)==0 && (iswspace(n[p.size()]) || n[p.size()]==L'-' || n[p.size()]==L'_'))) return "nvidia_or_shield";
    }
    return "other";
}
struct TextPropertyObservation { HRESULT hr=E_FAIL; VARTYPE type=VT_EMPTY; bool present=false; };
static std::wstring textProperty(IPropertyStore* store, REFPROPERTYKEY key, TextPropertyObservation* observation=nullptr) {
    PROPVARIANT v{}; std::wstring result;
    HRESULT hr=store->GetValue(key,&v);
    if (SUCCEEDED(hr) && v.vt==VT_LPWSTR && v.pwszVal && wcsnlen_s(v.pwszVal,2049)<=2048) result=v.pwszVal;
    if(observation)*observation={hr,v.vt,!result.empty()};
    PropVariantClear(&v); return result;
}
static bool usableMac(const std::array<BYTE,6>& bytes) {return (bytes[0]&1)==0&&std::any_of(bytes.begin(),bytes.end(),[](BYTE b){return b!=0;});}
static bool targetModel(const std::wstring& model,const std::wstring& manufacturer) {return classify(model)=="nvidia_blake"&&normalized(manufacturer)==L"ozmo";}
static std::string diagnosticClass(const std::wstring& raw,bool manufacturer=false) {return manufacturer?(normalized(raw)==L"ozmo"?"ozmo":"other"):classify(raw);}
static std::string propertyObservation(const TextPropertyObservation& value) {
    return "{\"hresult\":"+std::to_string(static_cast<unsigned>(value.hr))+",\"variant_type\":"+std::to_string(value.type)+",\"present\":"+jsonBool(value.present)+"}";
}
// Cached getters only: this neither starts a WPS exchange nor changes identity
// selection. Values are classified before logging; raw device strings/addresses
// and network settings never enter the observation record.
static void observeCachedDevice(IFunctionInstance* instance,const std::string& id,
    const TextPropertyObservation& friendly,const TextPropertyObservation& model,const TextPropertyObservation& manufacturer,
    HRESULT macHr,VARTYPE macType,bool macAvailable,const std::array<BYTE,6>& propertyMac) {
    ComPtr<IWCNDevice> device;HRESULT hr=instance->QueryService(SID_WcnProvider,IID_PPV_ARGS(&device));
    std::string record="{\"phase\":\"cached_discovery_observation\",\"id_hash\":\""+id+"\",\"property_name\":"+propertyObservation(friendly)+
        ",\"property_model\":"+propertyObservation(model)+",\"property_manufacturer\":"+propertyObservation(manufacturer)+
        ",\"property_mac\":{\"hresult\":"+std::to_string(static_cast<unsigned>(macHr))+",\"variant_type\":"+std::to_string(macType)+",\"available\":"+jsonBool(macAvailable)+"},\"query_service_hresult\":"+std::to_string(static_cast<unsigned>(hr));
    if(SUCCEEDED(hr)&&device) {
        auto stringAttribute=[&](const char* name,WCN_ATTRIBUTE_TYPE type,bool isManufacturer=false) {
            wchar_t value[128]{};HRESULT result=device->GetStringAttribute(type,128,value);
            bool present=SUCCEEDED(result)&&value[0]!=L'\0'&&wcsnlen_s(value,128)<128;
            record+=",\""+std::string(name)+"\":{\"hresult\":"+std::to_string(static_cast<unsigned>(result))+",\"present\":"+jsonBool(present)+",\"class\":\""+(present?diagnosticClass(value,isManufacturer):"other")+"\"}";
        };
        auto integerAttribute=[&](const char* name,WCN_ATTRIBUTE_TYPE type,UINT maximum) {
            UINT value=0;HRESULT result=device->GetIntegerAttribute(type,&value);bool available=SUCCEEDED(result)&&value<=maximum;
            record+=",\""+std::string(name)+"\":{\"hresult\":"+std::to_string(static_cast<unsigned>(result))+",\"available\":"+jsonBool(available)+",\"value\":"+(available?std::to_string(value):"null")+"}";
        };
        stringAttribute("cached_name",WCN_TYPE_DEVICE_NAME);
        stringAttribute("cached_model",WCN_TYPE_MODEL_NAME);
        stringAttribute("cached_manufacturer",WCN_TYPE_MANUFACTURER,true);
        integerAttribute("rf_bands",WCN_TYPE_RF_BANDS,7);
        integerAttribute("ap_channel",WCN_TYPE_AP_CHANNEL,233);
        integerAttribute("configuration_methods",WCN_TYPE_CONFIG_METHODS,65535);
        integerAttribute("device_password_id",WCN_TYPE_DEVICE_PASSWORD_ID,65535);
        integerAttribute("request_type",WCN_TYPE_REQUEST_TYPE,3);
        integerAttribute("selected_registrar",WCN_TYPE_SELECTED_REGISTRAR,1);
        BYTE mac[6]{};DWORD used=0;HRESULT result=device->GetAttribute(WCN_TYPE_MAC_ADDRESS,6,mac,&used);
        bool available=SUCCEEDED(result)&&used==6;
        record+=",\"cached_mac\":{\"hresult\":"+std::to_string(static_cast<unsigned>(result))+",\"available\":"+jsonBool(available)+",\"matches_property\":"+jsonBool(available&&macAvailable&&std::equal(propertyMac.begin(),propertyMac.end(),mac))+"}";
        BYTE primary[8]{};used=0;result=device->GetAttribute(WCN_TYPE_PRIMARY_DEVICE_TYPE,8,primary,&used);
        available=SUCCEEDED(result)&&used==8;unsigned category=(static_cast<unsigned>(primary[0])<<8)|primary[1];
        record+=",\"primary_device_category\":{\"hresult\":"+std::to_string(static_cast<unsigned>(result))+",\"available\":"+jsonBool(available)+",\"value\":"+(available?std::to_string(category):"null")+"}";
    }
    line(record+",\"cached_getters_only\":true,\"identity_selection_unchanged\":true,\"raw_identifiers_logged\":false}");
}
class Notify final : public IFunctionDiscoveryNotification {
    std::atomic<ULONG> refs{1}; std::mutex guard; std::set<std::string> ids; std::array<BYTE,6> expected;
    std::set<std::string> observedMetadata;
    ComPtr<IFunctionInstance> target; std::set<std::string> liveTargets; std::string targetId; bool stopping=false; bool discoverOnly;
public:
    std::atomic<unsigned> updates{0}, errors{0}, callbackErrors{0}, targets{0}, matches{0};
    std::atomic<bool> released{false};
    explicit Notify(std::array<BYTE,6> mac,bool discover):expected(mac),discoverOnly(discover) {}
    ULONG count() const { return refs.load(); }
    // Function Discovery reports changes, not periodic heartbeats. Keep the
    // uniquely identified target from this query until removal, contradiction,
    // ambiguity or shutdown; adapter verification may legitimately take >5s.
    ComPtr<IFunctionInstance> SelectedTarget() { std::lock_guard<std::mutex> lock(guard); return !stopping && liveTargets.size()==1 && liveTargets.count(targetId)==1 && target ? target : ComPtr<IFunctionInstance>{}; }
    std::string SelectedTargetId() { std::lock_guard<std::mutex> lock(guard); return !stopping && liveTargets.size()==1 && liveTargets.count(targetId)==1 && target ? targetId : ""; }
    void StopTargets() { std::lock_guard<std::mutex> lock(guard); stopping=true; target.Reset(); targetId.clear(); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void** out) override {
        if (!out) return E_POINTER; *out=nullptr;
        if (id==__uuidof(IUnknown) || id==__uuidof(IFunctionDiscoveryNotification)) { *out=static_cast<IFunctionDiscoveryNotification*>(this); AddRef(); return S_OK; } return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs; }
    ULONG STDMETHODCALLTYPE Release() override { ULONG n=--refs; if (!n) delete this; return n; }
    HRESULT STDMETHODCALLTYPE OnUpdate(QueryUpdateAction action, FDQUERYCONTEXT, IFunctionInstance* instance) override {
        try {
            ++updates; if (!instance) return S_OK;
            PWSTR raw=nullptr; HRESULT idHr=instance->GetID(&raw); std::string id;
            if (raw) { std::wstring owned; if (SUCCEEDED(idHr) && wcsnlen_s(raw,4097)<=4096) owned=raw; CoTaskMemFree(raw); if (!owned.empty()) id=hash(owned); }
            if (action==QUA_REMOVE) {
                { std::lock_guard<std::mutex> lock(guard); if(id.empty() || liveTargets.erase(id)!=0) { target.Reset(); targetId.clear(); if(id.empty())liveTargets.clear(); } }
                line("{\"phase\":\"device_removed\",\"id_hash\":\""+id+"\"}");return S_OK;
            }
            ComPtr<IPropertyStore> store; HRESULT hr=instance->OpenPropertyStore(STGM_READ, &store);
            if (FAILED(hr) || !store) {
                { std::lock_guard<std::mutex> lock(guard); if(id.empty() || liveTargets.erase(id)!=0) { target.Reset(); targetId.clear(); if(id.empty())liveTargets.clear(); } }
                line("{\"phase\":\"property_error\",\"hresult\":"+std::to_string(static_cast<unsigned>(hr))+"}");return S_OK;
            }
            TextPropertyObservation friendlyObservation,modelObservation,manufacturerObservation;
            auto friendly=textProperty(store.Get(),PKEY_DeviceDisplay_FriendlyName,&friendlyObservation);
            auto model=textProperty(store.Get(),PKEY_DeviceDisplay_ModelName,&modelObservation);
            auto manufacturer=textProperty(store.Get(),PKEY_DeviceDisplay_Manufacturer,&manufacturerObservation);
            auto nc=classify(friendly), mc=classify(model); bool ozmo=normalized(manufacturer)==L"ozmo";
            bool possible=nc!="other" || mc!="other";
            PROPVARIANT mac{}; bool available=false, equal=false; std::array<BYTE,6> observed{};
            HRESULT macHr=store->GetValue(PKEY_PNPX_PhysicalAddress,&mac);
            if (SUCCEEDED(macHr) && mac.vt==(VT_VECTOR|VT_UI1) && mac.caub.cElems==6 && mac.caub.pElems) {
                std::copy_n(mac.caub.pElems,6,observed.begin());
                available=usableMac(observed);
                equal=available&&std::equal(expected.begin(),expected.end(),observed.begin());
            }
            VARTYPE macType=mac.vt;PropVariantClear(&mac);
            if(discoverOnly&&available&&targetModel(model,manufacturer)&&!id.empty()) {
                std::string bytes;for(auto b:observed){if(!bytes.empty())bytes+=",";bytes+=std::to_string(b);}
                line("{\"phase\":\"private_candidate\",\"target_id_hash\":\""+id+"\",\"controller_mac\":["+bytes+"],\"model_verified\":true,\"manufacturer_verified\":true}");
            }
            { std::lock_guard<std::mutex> lock(guard);
              if (!id.empty()) ids.insert(id);
              bool exact=equal && targetModel(model,manufacturer) && !id.empty();
              if (action==QUA_REMOVE || !exact) { if(liveTargets.erase(id)!=0) { target.Reset(); targetId.clear(); } }
              else if (!stopping) { liveTargets.insert(id); target=instance; targetId=id; }
            }
            if (possible) ++targets; if (equal) ++matches;
            line("{\"phase\":\"device\",\"action\":"+std::to_string(static_cast<unsigned>(action))+",\"id_hash\":\""+id+"\",\"name_class\":\""+nc+"\",\"model_class\":\""+mc+"\",\"manufacturer_ozmo\":"+jsonBool(ozmo)+",\"possible_target\":"+jsonBool(possible)+",\"mac_available\":"+jsonBool(available)+",\"own_mac_equal\":"+jsonBool(equal)+",\"after_query_release\":"+jsonBool(released.load())+"}");
            bool observe=false;
            { std::lock_guard<std::mutex> lock(guard); if(!id.empty()&&observedMetadata.size()<64)observe=observedMetadata.insert(id).second; }
            if(observe)try { observeCachedDevice(instance,id,friendlyObservation,modelObservation,manufacturerObservation,macHr,macType,available,observed); }
            catch(...) { line("{\"phase\":\"cached_discovery_observation_error\",\"raw_identifiers_logged\":false}"); }
        } catch (...) { ++callbackErrors; line("{\"phase\":\"callback_error\"}"); } return S_OK;
    }
    HRESULT STDMETHODCALLTYPE OnError(HRESULT hr, FDQUERYCONTEXT, const WCHAR*) override {
        ++errors; try { line("{\"phase\":\"provider_error\",\"hresult\":"+std::to_string(static_cast<unsigned>(hr))+"}"); } catch (...) { ++callbackErrors; } return S_OK;
    }
    HRESULT STDMETHODCALLTYPE OnEvent(DWORD id, FDQUERYCONTEXT, const WCHAR*) override {
        try { line("{\"phase\":\"provider_event\",\"event_id\":"+std::to_string(id)+"}"); } catch (...) { ++callbackErrors; } return S_OK;
    }
    unsigned unique() { std::lock_guard<std::mutex> lock(guard); return static_cast<unsigned>(ids.size()); }
};
static void checkpoint(const char* phase,HRESULT hr) { line(std::string("{\"phase\":\"")+phase+"\",\"hresult\":"+std::to_string(static_cast<unsigned>(hr))+"}"); }
static bool parseMac(const wchar_t* raw,std::array<BYTE,6>& bytes) {
    if (wcslen(raw)!=12) return false;
    for (size_t i=0;i<6;++i) { unsigned n=0; for(size_t j=0;j<2;++j) { auto c=towlower(raw[i*2+j]); unsigned d=c>=L'0'&&c<=L'9'?c-L'0':c>=L'a'&&c<=L'f'?c-L'a'+10:99; if(d>15)return false; n=n*16+d; } bytes[i]=static_cast<BYTE>(n); } return true;
}

class ConnectNotify final : public IWCNConnectNotify {
    std::atomic<ULONG> refs{1};
public:
    std::atomic<int> terminal{0}; std::atomic<HRESULT> failure{S_OK};
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id,void** out) override {
        if(!out)return E_POINTER; *out=nullptr;
        if(id==__uuidof(IUnknown)||id==__uuidof(IWCNConnectNotify)){*out=static_cast<IWCNConnectNotify*>(this);AddRef();return S_OK;}return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override{return ++refs;}
    ULONG STDMETHODCALLTYPE Release() override{ULONG n=--refs;if(!n)delete this;return n;}
    ULONG count()const{return refs.load();}
    HRESULT STDMETHODCALLTYPE ConnectSucceeded() override { int zero=0; terminal.compare_exchange_strong(zero,1); line("{\"phase\":\"connect_succeeded\"}");return S_OK; }
    HRESULT STDMETHODCALLTYPE ConnectFailed(HRESULT hr) override { failure=hr; int zero=0; terminal.compare_exchange_strong(zero,2); try { checkpoint("connect_failed",hr); } catch(...){} return S_OK; }
};
static bool mandatoryIdentity(IWCNDevice* device,const std::array<BYTE,6>& expected) {
    BYTE mac[6]{};DWORD used=0;HRESULT hr=device->GetAttribute(WCN_TYPE_MAC_ADDRESS,6,mac,&used);checkpoint("cached_mac",hr);
    bool equal=SUCCEEDED(hr)&&used==6&&std::equal(expected.begin(),expected.end(),mac);
    UINT methods=0;HRESULT mh=device->GetIntegerAttribute(WCN_TYPE_CONFIG_METHODS,&methods);checkpoint("cached_configuration_methods",mh);
    UINT passwordId=0;HRESULT ph=device->GetIntegerAttribute(WCN_TYPE_DEVICE_PASSWORD_ID,&passwordId);checkpoint("cached_device_password_id",ph);
    bool push=SUCCEEDED(mh)&&(methods&WCN_VALUE_CM_PUSHBUTTON)!=0;
    // DevicePasswordID can be absent in discovery metadata. A contradictory present
    // value is rejected; neither an absent value nor any PIN is fabricated.
    bool passwordAccept=SUCCEEDED(ph)?passwordId==WCN_VALUE_DP_PUSHBUTTON:ph==HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
    wchar_t model[128]{},manufacturer[128]{};
    HRESULT modelHr=device->GetStringAttribute(WCN_TYPE_MODEL_NAME,128,model);checkpoint("cached_model",modelHr);
    HRESULT manufacturerHr=device->GetStringAttribute(WCN_TYPE_MANUFACTURER,128,manufacturer);checkpoint("cached_manufacturer",manufacturerHr);
    bool modelOk=SUCCEEDED(modelHr)?classify(model)=="nvidia_blake":modelHr==HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
    bool manufacturerOk=SUCCEEDED(manufacturerHr)?normalized(manufacturer)==L"ozmo":manufacturerHr==HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
    BYTE uuid[16]{};DWORD uuidUsed=0;HRESULT uh=device->GetAttribute(WCN_TYPE_UUID_E,16,uuid,&uuidUsed);checkpoint("cached_uuid_e",uh);
    std::wstring uuidHex;static const wchar_t* hex=L"0123456789abcdef";if(SUCCEEDED(uh)&&uuidUsed==16)for(BYTE b:uuid){uuidHex+=hex[b>>4];uuidHex+=hex[b&15];}
    line("{\"phase\":\"target_attributes\",\"own_mac_equal\":"+jsonBool(equal)+",\"configuration_methods_available\":"+jsonBool(SUCCEEDED(mh))+",\"configuration_methods\":"+std::to_string(methods)+",\"push_button_supported\":"+jsonBool(push)+",\"device_password_id_available\":"+jsonBool(SUCCEEDED(ph))+",\"device_password_id\":"+std::to_string(passwordId)+",\"model_consistent\":"+jsonBool(modelOk)+",\"manufacturer_consistent\":"+jsonBool(manufacturerOk)+",\"uuid_available\":"+jsonBool(!uuidHex.empty())+",\"uuid_hash\":\""+(uuidHex.empty()?"":hash(uuidHex))+"\"}");
    return equal&&push&&passwordAccept&&modelOk&&manufacturerOk;
}
static std::wstring readProfile() {
    std::string bytes;char c;while(std::cin.get(c)&&c!='\n'){if(bytes.size()>=8192)throw 1;bytes+=c;}
    if(bytes.empty())throw 1;
    int size=MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,bytes.data(),static_cast<int>(bytes.size()),nullptr,0);if(size<=0)throw 1;
    std::wstring result(static_cast<size_t>(size),L'\0');
    if(MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,bytes.data(),static_cast<int>(bytes.size()),result.data(),size)!=size)throw 1;
    SecureZeroMemory(bytes.data(),bytes.size());return result;
}
// This observes the provider's completed-session profile, not a new wireless
// transaction or proof that firmware persisted credentials. Never emit XML.
class PrivateBstr {
public:
    BSTR value=nullptr;
    explicit PrivateBstr(const std::wstring& s):value(SysAllocStringLen(s.data(),static_cast<UINT>(s.size()))){if(!value)throw 1;}
    ~PrivateBstr(){if(value){SecureZeroMemory(value,SysStringLen(value)*sizeof(wchar_t));SysFreeString(value);}}
    PrivateBstr(const PrivateBstr&)=delete;PrivateBstr& operator=(const PrivateBstr&)=delete;
};
static ComPtr<IXMLDOMDocument3> privateXml(const std::wstring& xml) {
    if(xml.empty()||xml.size()>16384)throw 1;
    ComPtr<IXMLDOMDocument3> doc;
    if(FAILED(CoCreateInstance(__uuidof(DOMDocument60),nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&doc))))throw 1;
    if(FAILED(doc->put_async(VARIANT_FALSE))||FAILED(doc->put_resolveExternals(VARIANT_FALSE))||FAILED(doc->put_validateOnParse(VARIANT_FALSE)))throw 1;
    PrivateBstr prohibit(L"ProhibitDTD");VARIANT flag{};flag.vt=VT_BOOL;flag.boolVal=VARIANT_TRUE;
    if(FAILED(doc->setProperty(prohibit.value,flag)))throw 1;
    PrivateBstr nsName(L"SelectionNamespaces"),nsValue(L"xmlns:w='http://www.microsoft.com/networking/WLAN/profile/v1'");VARIANT ns{};ns.vt=VT_BSTR;ns.bstrVal=nsValue.value;
    if(FAILED(doc->setProperty(nsName.value,ns)))throw 1;
    PrivateBstr text(xml);VARIANT_BOOL parsed=VARIANT_FALSE;
    if(FAILED(doc->loadXML(text.value,&parsed))||parsed!=VARIANT_TRUE)throw 1;
    return doc;
}
static std::wstring privateField(IXMLDOMDocument3* doc,const wchar_t* xpath) {
    PrivateBstr query(xpath);ComPtr<IXMLDOMNodeList> nodes;long count=0;
    if(FAILED(doc->selectNodes(query.value,&nodes))||!nodes||FAILED(nodes->get_length(&count))||count!=1)throw 1;
    ComPtr<IXMLDOMNode> node;if(FAILED(nodes->get_item(0,&node))||!node)throw 1;
    BSTR text=nullptr;HRESULT hr=node->get_text(&text);
    std::wstring result;
    if(SUCCEEDED(hr)&&text&&SysStringLen(text)<=8192)result.assign(text,SysStringLen(text));else hr=E_FAIL;
    if(text){SecureZeroMemory(text,SysStringLen(text)*sizeof(wchar_t));SysFreeString(text);}
    if(FAILED(hr))throw 1;return result;
}
struct ProfileComparison { bool parsed=false,ssid=false,authentication=false,encryption=false,keyType=false,keyProtection=false,keyMaterial=false; };
static ProfileComparison compareProfiles(const std::wstring& sent,const std::wstring& returned) {
    auto a=privateXml(sent),b=privateXml(returned);ProfileComparison result;
    auto equal=[&](const wchar_t* path){auto left=privateField(a.Get(),path),right=privateField(b.Get(),path);bool eq=left==right;SecureZeroMemory(left.data(),left.size()*sizeof(wchar_t));SecureZeroMemory(right.data(),right.size()*sizeof(wchar_t));return eq;};
    result.ssid=equal(L"/w:WLANProfile/w:SSIDConfig/w:SSID/w:name");
    result.authentication=equal(L"/w:WLANProfile/w:MSM/w:security/w:authEncryption/w:authentication");
    result.encryption=equal(L"/w:WLANProfile/w:MSM/w:security/w:authEncryption/w:encryption");
    result.keyType=equal(L"/w:WLANProfile/w:MSM/w:security/w:sharedKey/w:keyType");
    result.keyProtection=equal(L"/w:WLANProfile/w:MSM/w:security/w:sharedKey/w:protected");
    result.keyMaterial=equal(L"/w:WLANProfile/w:MSM/w:security/w:sharedKey/w:keyMaterial");result.parsed=true;return result;
}
static bool allEqual(const ProfileComparison& p){return p.parsed&&p.ssid&&p.authentication&&p.encryption&&p.keyType&&p.keyProtection&&p.keyMaterial;}
static void observeCompletedProfile(IWCNDevice* device,const std::wstring& sent) {
    std::array<wchar_t,WCN_API_MAX_BUFFER_SIZE> buffer{};HRESULT hr=device->GetNetworkProfile(static_cast<DWORD>(buffer.size()),buffer.data());
    checkpoint("get_completed_network_profile",hr);ProfileComparison comparison;bool terminated=false;
    size_t length=wcsnlen_s(buffer.data(),buffer.size());terminated=length<buffer.size();
    if(SUCCEEDED(hr)&&terminated&&length!=0){
        std::wstring returned(buffer.data(),length);
        try{comparison=compareProfiles(sent,returned);}catch(...){}
        SecureZeroMemory(returned.data(),returned.size()*sizeof(wchar_t));
    }
    SecureZeroMemory(buffer.data(),sizeof buffer);
    line("{\"phase\":\"completed_network_profile_comparison\",\"get_succeeded\":"+jsonBool(SUCCEEDED(hr))+",\"terminated\":"+jsonBool(terminated)+",\"parsed\":"+jsonBool(comparison.parsed)+",\"ssid_equals_sent\":"+jsonBool(comparison.ssid)+",\"authentication_equals_sent\":"+jsonBool(comparison.authentication)+",\"encryption_equals_sent\":"+jsonBool(comparison.encryption)+",\"key_type_equals_sent\":"+jsonBool(comparison.keyType)+",\"key_protection_equals_sent\":"+jsonBool(comparison.keyProtection)+",\"key_material_equals_sent\":"+jsonBool(comparison.keyMaterial)+",\"all_fields_equal\":"+jsonBool(allEqual(comparison))+",\"provider_session_cached_evidence\":true,\"device_persistence_proven\":false,\"xml_or_credentials_logged\":false}");
}
int wmain(int argc,wchar_t** argv) {
    if(argc==2&&std::wstring(argv[1])==L"--selftest") {
        std::array<BYTE,6> mac{};bool pass=classify(L"  nVidiaBlake  ")=="nvidia_blake"&&classify(L"PANdev")=="pandev"&&classify(L"NVIDIAevil")=="other"&&parseMac(L"001122aabbcc",mac)&&!parseMac(L"001122aabbzz",mac)&&WCN_PASSWORD_TYPE_PUSH_BUTTON==0&&WCN_VALUE_DP_PUSHBUTTON==4&&(WCN_VALUE_CM_PHYS_PUSHBUTTON&WCN_VALUE_CM_PUSHBUTTON)!=0;
        pass=pass&&usableMac({2,1,2,3,4,5})&&!usableMac({3,1,2,3,4,5})&&!usableMac({0,0,0,0,0,0})&&targetModel(L" nVidiaBlake ",L" OZMO ")&&!targetModel(L"NVIDIA",L"Ozmo")&&!targetModel(L"nVidiaBlake",L"Other");
        pass=pass&&diagnosticClass(L"private-router-name\"secret")=="other"&&diagnosticClass(L"private-manufacturer",true)=="other"&&diagnosticClass(L" Ozmo ",true)=="ozmo"&&diagnosticClass(L"nVidiaBlake")=="nvidia_blake";
        line(std::string("{\"phase\":\"selftest\",\"passed\":")+jsonBool(pass)+",\"checks\":18}");return pass?0:1;
    }
    if(argc==2&&std::wstring(argv[1])==L"--profile-selftest") {
        HRESULT init=CoInitializeEx(nullptr,COINIT_MULTITHREADED);if(FAILED(init))return 1;bool pass=false;
        try{
            std::wstring original=LR"(<WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1"><SSIDConfig><SSID><name>DIRECT-&amp;test</name></SSID></SSIDConfig><MSM><security><authEncryption><authentication>WPA2PSK</authentication><encryption>AES</encryption></authEncryption><sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>synthetic-password</keyMaterial></sharedKey></security></MSM></WLANProfile>)";
            auto same=compareProfiles(original,original);auto other=original;other.replace(other.find(L"synthetic-password"),18,L"changed-test-only");auto mismatch=compareProfiles(original,other);bool dtdRejected=false,wrongNsRejected=false,duplicateRejected=false;
            try{privateXml(L"<!DOCTYPE x [<!ENTITY y 'bad'>]><x>&y;</x>");}catch(...){dtdRejected=true;}
            try{compareProfiles(original,L"<WLANProfile/>");}catch(...){wrongNsRejected=true;}
            auto duplicate=original;duplicate.insert(duplicate.find(L"</SSID>"),L"<name>second</name>");try{compareProfiles(original,duplicate);}catch(...){duplicateRejected=true;}
            pass=allEqual(same)&&mismatch.parsed&&!mismatch.keyMaterial&&mismatch.ssid&&dtdRejected&&wrongNsRejected&&duplicateRejected;
        }catch(...){}CoUninitialize();line(std::string("{\"phase\":\"profile_selftest\",\"passed\":")+jsonBool(pass)+",\"checks\":5,\"wireless_calls\":0}");return pass?0:1;
    }
    if(argc!=3||std::wstring(argv[1])!=L"--internal-query"||(std::wstring(argv[2])!=L"pair"&&std::wstring(argv[2])!=L"discover")){line("{\"phase\":\"plan\",\"activation\":false,\"instruction\":\"Use ShieldControllerOwner setup\"}");return argc==1?0:2;}
    // Last-resort lifetime bound even if the supervisor dies or a native COM
    // call never returns. Forced process exit does not prove provisioning rollback.
    std::thread([]{std::this_thread::sleep_for(std::chrono::seconds(300));ExitProcess(89);}).detach();
    std::array<BYTE,6> expected{};bool pair=std::wstring(argv[2])==L"pair";std::wstring profile;
    try{if(pair){std::string macLine;std::getline(std::cin,macLine);if(macLine.size()!=12)throw 1;std::wstring raw(macLine.begin(),macLine.end());if(!parseMac(raw.c_str(),expected)||!usableMac(expected))throw 1;profile=readProfile();}}
    catch(...){line("{\"phase\":\"private_profile_input_rejected\"}");return 2;}
    HRESULT hr=CoInitializeEx(nullptr,COINIT_MULTITHREADED);checkpoint("com_initialize",hr);if(FAILED(hr))return 1;
    auto notify=new Notify(expected,!pair);auto connectNotify=new ConnectNotify();
    bool executed=false,found=false,identity=false,connectAttempted=false,connectAccepted=false,pairTimedOut=false,unadvised=false;
    bool noProviderRefs=false,noConnectRefs=false;HRESULT executeHr=E_FAIL,connectHr=E_FAIL;unsigned exitCode=1;
    {
        ComPtr<IFunctionDiscovery> discovery;ComPtr<IFunctionInstanceCollectionQuery> query;ComPtr<IFunctionInstanceCollection> collection;ComPtr<IFunctionInstance> target;ComPtr<IWCNDevice> device;
        try {
            hr=CoCreateInstance(__uuidof(FunctionDiscovery),nullptr,CLSCTX_INPROC_SERVER|CLSCTX_NO_CODE_DOWNLOAD,IID_PPV_ARGS(&discovery));checkpoint("create_function_discovery",hr);
            if(SUCCEEDED(hr)){hr=discovery->CreateInstanceCollectionQuery(FCTN_CATEGORY_WCN,nullptr,FALSE,notify,nullptr,&query);checkpoint("create_wcn_query",hr);}
            if(SUCCEEDED(hr)){hr=query->AddQueryConstraint(WCN_QUERY_CONSTRAINT_USE_SOFTAP,FD_CONSTRAINTVALUE_TRUE);checkpoint("add_softap_constraint",hr);}
            if(SUCCEEDED(hr)){
                line("{\"phase\":\"execute_begin\",\"softap_requested\":true,\"discovery_seconds\":"+std::to_string(DiscoverySeconds)+"}");executed=true;executeHr=query->Execute(&collection);checkpoint("execute_return",executeHr);
                if(SUCCEEDED(executeHr)||executeHr==E_PENDING){
                    auto until=std::chrono::steady_clock::now()+std::chrono::seconds(DiscoverySeconds);
                    do{target=notify->SelectedTarget();if(target)break;std::this_thread::sleep_for(std::chrono::milliseconds(100));}while(std::chrono::steady_clock::now()<until);
                }
            }
            found=static_cast<bool>(target);std::string retainedId;
            if(found){
                PWSTR rawId=nullptr;hr=target->GetID(&rawId);std::wstring ownedId;
                if(rawId){if(SUCCEEDED(hr)&&wcsnlen_s(rawId,4097)<=4096)ownedId=rawId;CoTaskMemFree(rawId);}
                if(!ownedId.empty())retainedId=hash(ownedId);
                hr=target->QueryService(SID_WcnProvider,IID_PPV_ARGS(&device));checkpoint("query_iwcndevice",hr);if(SUCCEEDED(hr)&&device)identity=mandatoryIdentity(device.Get(),expected);
            }
            bool sameSelectedTarget=!retainedId.empty()&&notify->SelectedTargetId()==retainedId;
            if(!sameSelectedTarget)identity=false;
            line("{\"phase\":\"pre_provision_target_check\",\"same_selected_target\":"+jsonBool(sameSelectedTarget)+",\"id_hash\":\""+retainedId+"\"}");
            if(pair&&identity&&sameSelectedTarget&&notify->errors==0&&notify->callbackErrors==0){
                // Supervisor rechecks the selected radio/uplink and any existing
                // bootstrap GO. Discovery does not promise to create a GO.
                line("{\"phase\":\"provision_ready\"}");std::string approval;std::getline(std::cin,approval);
                if(approval!="approve-provisioning"||notify->SelectedTargetId()!=retainedId)throw 1;
                line("{\"phase\":\"private_profile_queue_begin\"}");hr=device->SetNetworkProfile(profile.c_str());checkpoint("set_network_profile",hr);
                // Retain only until GetNetworkProfile after successful completion;
                // never serialize this private input or the returned XML.
                if(SUCCEEDED(hr)){hr=device->SetPassword(WCN_PASSWORD_TYPE_PUSH_BUTTON,0,nullptr);checkpoint("set_push_button",hr);}
                if(SUCCEEDED(hr)){
                    line("{\"phase\":\"connect_begin\",\"pair_seconds\":"+std::to_string(ProvisioningSeconds)+",\"no_supported_cancel_or_rollback\":true}");connectAttempted=true;connectHr=device->Connect(connectNotify);checkpoint("connect_return",connectHr);connectAccepted=SUCCEEDED(connectHr);
                    if(connectAccepted){auto until=std::chrono::steady_clock::now()+std::chrono::seconds(ProvisioningSeconds);while(connectNotify->terminal==0&&std::chrono::steady_clock::now()<until)std::this_thread::sleep_for(std::chrono::milliseconds(100));pairTimedOut=connectNotify->terminal==0;if(connectNotify->terminal==1){observeCompletedProfile(device.Get(),profile);line("{\"phase\":\"post_success_observation_begin\",\"seconds\":5,\"set_calls\":0,\"device_persistence_proven\":false}");std::this_thread::sleep_for(std::chrono::seconds(5));line("{\"phase\":\"post_success_observation_end\",\"seconds\":5,\"set_calls\":0}");}}
                }
            }
        }catch(...){line("{\"phase\":\"worker_exception\"}");}
        // Unadvise must be outside callbacks. It removes notifications, not the wire
        // operation, and cannot undo credentials already provisioned to the target.
        if(device&&connectAttempted){HRESULT uh=device->Unadvise();checkpoint("unadvise_not_cancel",uh);unadvised=SUCCEEDED(uh);}
        device.Reset();target.Reset();notify->StopTargets();collection.Reset();query.Reset();discovery.Reset();notify->released=true;
        line("{\"phase\":\"query_collection_factory_released\"}");
        for(int n=0;n<20&&(notify->count()!=1||connectNotify->count()!=1);++n)std::this_thread::sleep_for(std::chrono::milliseconds(100));
        noProviderRefs=notify->count()==1;noConnectRefs=connectNotify->count()==1;
        bool pairingSuccess=connectNotify->terminal==1;bool unknown=connectAttempted&&connectAccepted&&connectNotify->terminal==0;
        bool ok=executed&&(SUCCEEDED(executeHr)||executeHr==E_PENDING)&&noProviderRefs&&noConnectRefs&&notify->errors==0&&notify->callbackErrors==0&&(!pair||(found&&identity&&pairingSuccess));
        line("{\"phase\":\"complete\",\"ok\":"+jsonBool(ok)+",\"executed\":"+jsonBool(executed)+",\"target_found\":"+jsonBool(found)+",\"identity_confirmed\":"+jsonBool(identity)+",\"provider_callback_references_released\":"+jsonBool(noProviderRefs)+",\"connect_callback_references_released\":"+jsonBool(noConnectRefs)+",\"unique_devices\":"+std::to_string(notify->unique())+",\"possible_target_events\":"+std::to_string(notify->targets.load())+",\"own_mac_match_events\":"+std::to_string(notify->matches.load())+",\"provider_errors\":"+std::to_string(notify->errors.load())+",\"callback_errors\":"+std::to_string(notify->callbackErrors.load())+",\"connect_attempted\":"+jsonBool(connectAttempted)+",\"connect_accepted\":"+jsonBool(connectAccepted)+",\"connect_hresult\":"+std::to_string(static_cast<unsigned>(connectHr))+",\"pairing_success\":"+jsonBool(pairingSuccess)+",\"pairing_terminal_callback\":"+std::to_string(connectNotify->terminal.load())+",\"pairing_timed_out\":"+jsonBool(pairTimedOut)+",\"provision_outcome_unknown\":"+jsonBool(unknown)+",\"unadvised_not_cancelled\":"+jsonBool(unadvised)+",\"candidate_credentials_retained\":true,\"wireless_input_proven\":false}");exitCode=ok?0:1;
    }
    if(!profile.empty())SecureZeroMemory(profile.data(),profile.size()*sizeof(wchar_t));
    notify->Release();connectNotify->Release();CoUninitialize();line("{\"phase\":\"com_uninitialized\"}");return static_cast<int>(exitCode);
}
